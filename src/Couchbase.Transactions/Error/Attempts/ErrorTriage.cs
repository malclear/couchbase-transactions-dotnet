﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Couchbase.Core.Exceptions;
using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.KeyValue;
using Couchbase.Transactions.Error.external;
using Couchbase.Transactions.Error.Internal;
using Couchbase.Transactions.Internal.Test;
using Microsoft.Extensions.Logging;
using static Couchbase.Transactions.Error.ErrorBuilder;
using static Couchbase.Transactions.Error.ErrorClass;
using static Couchbase.Transactions.Error.Internal.TransactionOperationFailedException.FinalError;

namespace Couchbase.Transactions.Error.Attempts
{
    internal class ErrorTriage
    {
        private readonly AttemptContext _ctx;
        private readonly ITestHooks _testHooks;
        private readonly ILogger? _logger;

        public ErrorTriage(AttemptContext ctx, ITestHooks testHooks, ILoggerFactory? loggerFactory)
        {
            _ctx = ctx;
            _testHooks = testHooks;
            _logger = loggerFactory?.CreateLogger(nameof(ErrorTriage));
        }

        public TransactionOperationFailedException AssertNotNull(TransactionOperationFailedException? toThrow, ErrorClass ec, Exception innerException) =>
            toThrow ?? CreateError(_ctx, ec,
                    new InvalidOperationException("Failed to generate proper exception wrapper", innerException))
                .Build();

        public TransactionOperationFailedException AssertNotNull(
            (ErrorClass ec, TransactionOperationFailedException? toThrow) triageResult, Exception innerException) =>
            AssertNotNull(triageResult.toThrow, triageResult.ec, innerException);

        private ErrorBuilder Error(ErrorClass ec, Exception err, bool? retry = null, bool? rollback = null, TransactionOperationFailedException.FinalError? raise = null)
        {
            var eb = CreateError(_ctx, ec, err);
            if (retry.HasValue && retry.Value)
            {
                eb.RetryTransaction();
            }

            if (rollback.HasValue && rollback.Value == false)
            {
                eb.DoNotRollbackAttempt();
            }

            if (raise.HasValue)
            {
                eb.RaiseException(raise.Value);
            }

            return eb;
        }

        public (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageGetErrors(Exception err)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A?view#GetOptionalAsync
            // On error err of any of the above, classify as ErrorClass ec then:
            //   FAIL_DOC_NOT_FOUND -> return empty
            //   Else FAIL_HARD -> Error(ec, err, rollback=false)
            //   Else FAIL_TRANSIENT -> Error(ec, err, retry=true)
            //   Else -> raise Error(ec, cause=err)

            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailDocNotFound => None,
                FailHard => Error(ec, err, rollback: false),
                FailTransient => Error(ec, err, retry:false),
                _ => Error(ec, err)
            };

            return (ec, toThrow?.Build());
        }

        public (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageCreateStagedRemoveOrReplaceError(Exception err)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#Creating-Staged-ReplaceAsync
            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailExpiry => Error(ec, new AttemptExpiredException(_ctx), raise: TransactionExpired),
                FailDocNotFound => Error(ec, err, retry: true),
                FailCasMismatch => Error(ec, err, retry: true),
                FailTransient => Error(ec, err, retry: true),
                FailAmbiguous => Error(ec, err, retry: true),
                FailHard => Error(ec, err, rollback: false),
                 _ => Error(ec, err)
            };

            return (ec, toThrow?.Build());
        }

        public (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageCreateStagedInsertErrors(Exception err, in bool expirationOvertimeMode)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#Creating-Staged-Inserts-Protocol-20-version

            if (err is FeatureNotAvailableException)
            {
                // If err is FeatureNotFoundException, then this cluster does not support creating shadow
                // documents. (Unfortunately we cannot perform this check at the Transactions.create point,
                // as we may not have a cluster config available then).
                // Raise Error(ec=FAIL_OTHER, cause=err) to terminate the transaction.
                return (FailOther, Error(FailOther, err).Build());
            }

            if (expirationOvertimeMode)
            {
                return (FailExpiry,
                    Error(FailExpiry, new AttemptExpiredException(_ctx), rollback: false, raise: TransactionExpired)
                        .Build());
            }

            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailExpiry => Error(ec, new AttemptExpiredException(_ctx), raise: TransactionExpired),
                FailAmbiguous => null, // retry after delay
                FailTransient => Error(ec, err, retry: true),
                FailHard => Error(ec, err, rollback: false),
                FailCasMismatch => null, // handles the same as FailDocAlreadyExists
                FailDocAlreadyExists => null, // special handling
                _ => Error(ec, err)
            };

            return (ec, toThrow?.Build());
        }

        public (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageDocExistsOnStagedInsertErrors(Exception err)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#Creating-Staged-Inserts-Protocol-20-version
            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailDocNotFound => Error(ec, err, retry: true),
                FailPathNotFound => Error(ec, err, retry:true),
                FailTransient => Error(ec, err, retry: true),
                _ => Error(ec, err)
            };

            return (ec, toThrow?.Build());
        }

        public (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageSetAtrPendingErrors(Exception err, in bool expirationOvertimeMode)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#Creating-Staged-Inserts-Protocol-20-version
            if (expirationOvertimeMode)
            {
                return (FailExpiry,
                    Error(FailExpiry, new AttemptExpiredException(_ctx), rollback: false, raise: TransactionExpired)
                        .Build());
            }

            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailExpiry => Error(ec, new AttemptExpiredException(_ctx), raise: TransactionExpired),
                FailAtrFull => Error(ec, new ActiveTransactionRecordsFullException(_ctx)),
                FailAmbiguous => null, // retry from the top of section
                FailPathAlreadyExists => null, // treat as successful
                FailHard => Error(ec, err, rollback: false),
                FailTransient => Error(ec, err, retry:true),
                _ => Error(ec, err)
            };

            return (ec, toThrow?.Build());
        }

        internal void ThrowIfCommitWithPreviousErrors(IEnumerable<TransactionOperationFailedException> previousErrorOperations)
        {
            // We have potentially multiple ErrorWrappers, and will construct a new single ErrorWrapper err from them using this algo:
            // err.retryTransaction is true iff it’s true for ALL errors.
            // err.rollback is true iff it’s true for ALL errors.
            // err.cause = PreviousOperationFailed, with that exception taking and providing access to the causes of all the errors
            // Then, raise err
            var previousErrors = previousErrorOperations.ToList();
            var retryTransaction = previousErrors.All(ex => ex.RetryTransaction);
            var rollback = previousErrors.All(ex => ex.AutoRollbackAttempt);
            var cause = new PreviousOperationFailedException(previousErrors);
            var builder = CreateError(_ctx, TransactionOperationFailed, cause);
            if (retryTransaction)
            {
                builder.RetryTransaction();
            }

            if (!rollback)
            {
                builder.DoNotRollbackAttempt();
            }

            throw builder.Build();
        }

        public (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageSetAtrCompleteErrors(Exception err)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#SetATRComplete
            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailHard => Error(ec, err, rollback: false),
                // Setting the ATR to COMPLETED is purely a cleanup step, there’s no need to retry it until expiry.
                // Simply return success (leaving state at COMMITTED).
                _ => null,
            };

            return (ec, toThrow?.Build());
        }

        public (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageUnstageRemoveErrors(Exception err, in bool expirationOvertimeMode)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#Unstaging-Removes
            if (expirationOvertimeMode)
            {
                return (FailExpiry,
                    Error(FailExpiry, new AttemptExpiredException(_ctx), rollback: false, raise: TransactionFailedPostCommit)
                        .Build());
            }

            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailAmbiguous => null, // retry after opRetryDelay
                FailDocNotFound => Error(ec, err, rollback:false, raise: TransactionFailedPostCommit),
                FailHard => Error(ec, err, rollback: false),
                _ => Error(ec, err, rollback: false, raise: TransactionFailedPostCommit)
            };

            return (ec, toThrow?.Build());
        }

        public (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageUnstageInsertOrReplaceErrors(Exception err, in bool expirationOvertimeMode)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#Unstaging-Inserts-and-Replaces-Protocol-20-version
            if (expirationOvertimeMode)
            {
                return (FailExpiry,
                    Error(FailExpiry, new AttemptExpiredException(_ctx), rollback: false, raise: TransactionFailedPostCommit)
                        .Build());
            }

            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailAmbiguous => null, // retry
                FailCasMismatch => Error(ec, err, rollback:false, raise:TransactionFailedPostCommit),
                FailDocNotFound => null, // retry
                FailDocAlreadyExists => Error(ec, err, rollback: false, raise: TransactionFailedPostCommit),
                _ => Error(ec, err, rollback: false, raise: TransactionFailedPostCommit)
            };

            return (ec, toThrow?.Build());
        }

        internal (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageSetAtrCommitErrors(Exception err)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#SetATRCommit
            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailExpiry => Error(ec, new AttemptExpiredException(_ctx, "Attempt Expired in SetATRCommit", err), raise: TransactionExpired),
                // FailAmbiguous to be handled later in SetATRCommit Ambiguity Resolution
                FailAmbiguous => null,
                FailHard => Error(ec, err, rollback: false),
                FailTransient => Error(ec, err, retry: true),
                _ => Error(ec, err)
            };

            return (ec, toThrow?.Build());
        }

        internal (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageSetAtrCommitAmbiguityErrors(Exception err)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#SetATRCommit-Ambiguity-Resolution
            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailExpiry => Error(ec, new AttemptExpiredException(_ctx,
                        "Attempt expired during SetAtrCommit ambiguity resolution.", err), rollback: false, raise: TransactionCommitAmbiguous),
                FailHard => Error(ec, err, rollback: false),
                // FailTransient, FailOther result in retry of SetAtrCommit Ambiguity Resolution
                FailTransient => null,
                FailOther => null,
                _ => Error(ec, err, rollback: false)
            };

            return (ec, toThrow?.Build());
        }

        public (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageSetAtrAbortedErrors(Exception err)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#SetATRAborted
            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailExpiry => null,
                FailPathNotFound => Error(ec, new ActiveTransactionRecordEntryNotFoundException(), rollback: false),
                FailDocNotFound => Error(ec, new ActiveTransactionRecordNotFoundException(), rollback: false),
                FailAtrFull => Error(ec, new ActiveTransactionRecordsFullException(_ctx, "ATR Full during SetAtrAborted."), rollback: false),
                FailHard => Error(ec, err, rollback: false),
                _ => null
            };

            return (ec, toThrow?.Build());
        }

        public (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageSetAtrRolledBackErrors(Exception err)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#SetATRRolledBack
            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailExpiry => null,
                FailPathNotFound => null,
                FailDocNotFound => Error(ec, new ActiveTransactionRecordNotFoundException(), rollback: false),
                FailAtrFull => Error(ec, new ActiveTransactionRecordsFullException(_ctx, "ATR Full during SetAtrRolledBack."), rollback: false),
                FailHard => Error(ec, err, rollback: false),
                _ => null
            };

            return (ec, toThrow?.Build());
        }

        public (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageRollbackStagedInsertErrors(Exception err)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A?view#RollbackAsync-Staged-InsertAsync
            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailExpiry => null,
                FailPathNotFound => null,
                FailDocNotFound => null,
                FailCasMismatch => Error(ec, err, rollback: false),
                FailHard => Error(ec, err, rollback: false),
                _ => null
            };

            return (ec, toThrow?.Build());
        }

        internal (ErrorClass ec, TransactionOperationFailedException? toThrow) TriageRollbackStagedRemoveOrReplaceErrors(Exception err)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A?view#RollbackAsync-Staged-ReplaceAsync-or-RemoveAsync
            var ec = err.Classify();
            ErrorBuilder? toThrow = ec switch
            {
                FailExpiry => null,
                FailPathNotFound => null,
                FailDocNotFound => Error(ec, err, rollback: false),
                FailCasMismatch => Error(ec, err, rollback: false),
                FailHard => Error(ec, err, rollback: false),
                _ => null
            };

            return (ec, toThrow?.Build());
        }
    }
}