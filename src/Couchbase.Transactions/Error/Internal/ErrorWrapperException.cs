﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Couchbase.Transactions.Error.Internal
{
    internal class ErrorWrapperException : CouchbaseException, IClassifiedTransactionError
    {
        private readonly AttemptContext _ctx;
        public ErrorClass CausingErrorClass { get; }
        public bool AutoRollbackAttempt { get; }
        public bool RetryTransaction { get; }
        public Exception Cause { get; }
        internal FinalError FinalErrorToRaise { get; }

        internal enum FinalError
        {
            TransactionFailed = 0,
            TransactionExpired = 1,
            TransactionCommitAmbiguous = 2,

            /**
             * This will currently result in returning success to the application, but unstagingCompleted() will be false.
             */
            TransactionFailedPostCommit = 3
        }

        public ErrorWrapperException(
            AttemptContext ctx,
            ErrorClass causingErrorClass,
            bool autoRollbackAttempt,
            bool retryTransaction,
            Exception cause,
            FinalError finalErrorToRaise)
        {
            _ctx = ctx;
            CausingErrorClass = causingErrorClass;
            AutoRollbackAttempt = autoRollbackAttempt;
            RetryTransaction = retryTransaction;
            Cause = cause;
            FinalErrorToRaise = finalErrorToRaise;
        }
    }
}