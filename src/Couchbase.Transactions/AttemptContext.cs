﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Couchbase.Core;
using Couchbase.Core.Exceptions;
using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.Core.IO.Operations;
using Couchbase.Core.IO.Transcoders;
using Couchbase.Core.Logging;
using Couchbase.KeyValue;
using Couchbase.Transactions.ActiveTransactionRecords;
using Couchbase.Transactions.Config;
using Couchbase.Transactions.Error;
using Couchbase.Transactions.Error.Internal;
using Couchbase.Transactions.Internal.Test;
using Couchbase.Transactions.Log;
using Couchbase.Transactions.Support;
using DnsClient.Internal;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Couchbase.Transactions.Error.ErrorBuilder;

namespace Couchbase.Transactions
{
    public class AttemptContext
    {
        private readonly TransactionContext _overallContext;
        private readonly TransactionConfig _config;
        private readonly Transactions _parent;
        private readonly ITestHooks _testHooks;
        private readonly IRedactor _redactor;
        private readonly ITypeTranscoder _transcoder;
        private AttemptStates _state = AttemptStates.NothingWritten;

        // TODO:  Should this be a Dictionary keyed on bucket::scope::collection::id?
        private List<StagedMutation> _stagedMutations = new List<StagedMutation>();
        private readonly string _attemptId;

        private object _initAtrLock = new object();
        private string _atrId = null;
        private string _atrBucketName = null;
        private string _atrCollectionName = null;
        private string _atrScopeName = null;
        private string _atrLongCollectionName = null;
        private readonly DurabilityLevel _effectiveDurabilityLevel;
        private readonly Dictionary<string, MutationToken> _finalMutations = new Dictionary<string, MutationToken>();
        private ICouchbaseCollection _atrCollection;

        internal AttemptContext(TransactionContext overallContext,
            TransactionConfig config,
            string attemptId,
            Transactions parent,
            ITestHooks testHooks,
            IRedactor redactor,
            ITypeTranscoder transcoder)
        {
            _attemptId = attemptId;
            _overallContext = overallContext;
            _config = config;
            _parent = parent;
            _testHooks = testHooks;
            _redactor = redactor;
            _transcoder = transcoder;
            _effectiveDurabilityLevel = _overallContext.PerConfig?.DurabilityLevel ?? config.DurabilityLevel;
        }

        public ILogger<AttemptContext> Logger { get; }

        public async Task<TransactionGetResult?> GetOptional(ICouchbaseCollection collection, string id)
        {
            // TODO: Update this when adding non-throwing versions to NCBC itself so that GetOptional is the root and Get calls it instead.
            try
            {
                return await Get(collection, id).CAF();
            }
            catch (DocumentNotFoundException)
            {
                Logger?.LogInformation("Document '{id}' not found in collection '{collection.Name}'", _redactor.UserData(id), _redactor.UserData(collection));
                return null;
            }
        }

        public async Task<TransactionGetResult?> Get(ICouchbaseCollection collection, string id)
        {
            DoneCheck();
            CheckExpiry();
            var staged = FindStaged(collection, id);
            if (staged != null)
            {
                switch (staged.Type)
                {
                    case StagedMutationType.Insert:
                    case StagedMutationType.Replace:
                        // LOGGER.info(attemptId, "found own-write of mutated doc %s", RedactableArgument.redactUser(id));
                        return TransactionGetResult.FromOther(staged.Doc, staged.Content, TransactionJsonDocumentStatus.OwnWrite);
                    case StagedMutationType.Remove:
                        // LOGGER.info(attemptId, "found own-write of removed doc %s", RedactableArgument.redactUser(id));
                        return null;
                    default:
                        throw new InvalidOperationException($"Document '{_redactor.UserData(id)}' was staged with type {staged.Type}");
                }
            }

            await _testHooks.BeforeDocGet(this, id).CAF();


            try
            {
                var doc = await collection.LookupInAsync(
                    id, 
                    specs =>
                        specs.Get(TransactionFields.AtrId, true) // 0
                            .Get(TransactionFields.TransactionId, isXattr: true) // 1
                            .Get(TransactionFields.AttemptId, isXattr: true) // 2
                            .Get(TransactionFields.StagedData, isXattr: true) // 3
                            .Get(TransactionFields.AtrBucketName, isXattr: true) // 4
                            .Get(TransactionFields.AtrCollName, isXattr: true) // 5
                            .Get(TransactionFields.TransactionRestorePrefixOnly, true) // 6
                            .Get(TransactionFields.Type, true) // 7
                            .Get("$document", true) //  8
                            .GetFull(), // 9
                    opts => opts.Timeout(_config.KeyValueTimeout)    
                ).CAF();

                // TODO:  Not happy with this mess of logic spread between AttemptContext.cs and TransactionGetResult.FromLookupIn
                (string? casFromDocument, string? revIdFromDocument, ulong? expTimeFromDocument) = (null, null, null);
                if (doc.Exists(8))
                {
                    var docMeta = doc.ContentAs<JObject>(8);
                    casFromDocument = docMeta["CAS"].Value<string>();
                    revIdFromDocument = docMeta["revid"].Value<string>();
                    expTimeFromDocument = docMeta["exptime"].Value<ulong?>();
                }

                (string? casPreTxn, string? revIdPreTxn, ulong? expTimePreTxn) = (null, null, null);
                if (doc.Exists(6))
                {
                    var docMeta = doc.ContentAs<JObject>(6);
                    if (docMeta != null)
                    {
                        casPreTxn = docMeta["CAS"].Value<string>();
                        revIdPreTxn = docMeta["revid"].Value<string>();
                        expTimePreTxn = docMeta["exptime"].Value<ulong?>();
                    }
                }

                // HACK:  ContentAs<byte[]> is failing.
                ////var preTxnContent = doc.ContentAs<byte[]>(9);
                var asDynamic = doc.ContentAs<dynamic>(9);
                var preTxnContent = GetContentBytes(asDynamic);

                var getResult = TransactionGetResult.FromLookupIn(
                    collection,
                    id,
                    TransactionJsonDocumentStatus.Normal,
                    _transcoder,
                    doc.Cas,                    
                    preTxnContent,
                    atrId: StringIfExists(doc, 0),
                    transactionId: StringIfExists(doc, 1),
                    attemptId: StringIfExists(doc, 2),
                    stagedContent: StringIfExists(doc, 3),
                    atrBucketName: StringIfExists(doc, 4),
                    atrLongCollectionName: StringIfExists(doc, 5),
                    op: StringIfExists(doc, 7),
                    casPreTxn: casPreTxn,
                    revidPreTxn: revIdPreTxn,
                    exptimePreTxn: expTimePreTxn,
                    casFromDocument: casFromDocument,
                    revidFromDocument: revIdFromDocument,
                    exptimeFromDocument: expTimeFromDocument
                );

                await _testHooks.AfterGetComplete(this, id).CAF();

                return getResult;
            }
            catch (Exception e)
            {
                /* TODO:
                 * On error err of any of the above, classify as ErrorClass ec then:
FAIL_DOC_NOT_FOUND -> return empty
Else FAIL_HARD -> Error(ec, err, rollback=false)
Else FAIL_TRANSIENT -> Error(ec, err, retry=true)

                 */
                throw;
            }
        }

        private string? StringIfExists(ILookupInResult doc, int index) => doc.Exists(index) ? doc.ContentAs<string>(index) : null;

        private StagedMutation FindStaged(ICouchbaseCollection collection, string id)
        {
            return _stagedMutations.Find(sm => sm.Doc.Id == id
                                               && sm.Doc.Collection.Name == collection.Name
                                               && sm.Doc.Collection.Scope.Name == collection.Scope.Name
                                               && sm.Doc.Collection.Scope.Bucket.Name == collection.Scope.Bucket.Name);
        }

        private StagedMutation FindStaged(TransactionGetResult doc) => FindStaged(doc.Collection, doc.Id);

        public async Task<TransactionGetResult> Replace(TransactionGetResult doc, object content)
        {
            DoneCheck();
            CheckExpiry();
            CheckWriteWriteConflict(doc);
            InitAtrIfNeeded(doc.Collection, doc.Id);
            if (_stagedMutations.Count == 0)
            {
                var setAtrPendingResult = await SetAtrPending(doc.Collection);
            }

            // TODO: re-evaluate accessDeleted after CreateAsDeleted is implemented.
            return await CreateStagedReplace(doc, content, accessDeleted:false);
        }

        private async Task<TransactionGetResult> CreateStagedReplace(TransactionGetResult doc, object content, bool accessDeleted)
        {
            try
            {
                await _testHooks.BeforeStagedReplace(this, doc.Id);
                var contentBytes = GetContentBytes(content);
                var specs = CreateMutationOps("replace");
                specs.Add(MutateInSpec.Upsert(TransactionFields.StagedData, contentBytes, isXattr: true));

                if (doc.DocumentMetadata != null)
                {
                    var dm = doc.DocumentMetadata;
                    if (dm.Cas != null)
                    {
                        specs.Add(MutateInSpec.Upsert(TransactionFields.PreTxnCas, dm.Cas, isXattr: true));
                    }

                    if (dm.RevId != null)
                    {
                        specs.Add(MutateInSpec.Upsert(TransactionFields.PreTxnRevid, dm.RevId, isXattr: true));
                    }

                    if (dm.ExpTime != null)
                    {
                        specs.Add(MutateInSpec.Upsert(TransactionFields.PreTxnExptime, dm.ExpTime, isXattr: true));
                    }
                }

                var opts = new MutateInOptions()
                    .Cas(doc.Cas)
                    .StoreSemantics(StoreSemantics.Replace)
                    .Durability(_effectiveDurabilityLevel );

                var updatedDoc = await doc.Collection.MutateInAsync(doc.Id, specs, opts).CAF();
                await _testHooks.AfterStagedReplaceComplete(this, doc.Id).CAF();

                doc.Cas = updatedDoc.Cas;

                ////LOGGER.info(attemptId, "replaced doc %s got %s, in %dms",
                ////    RedactableArgument.redactUser(doc.id()), dbg(updatedDoc), elapsed);

                var stagedOld = FindStaged(doc);
                if (stagedOld != null)
                {
                    _stagedMutations.Remove(stagedOld);
                }
                
                if (stagedOld?.Type == StagedMutationType.Insert)
                {
                    // If doc is already in stagedMutations as an INSERT or INSERT_SHADOW, then remove that, and add this op as a new INSERT or INSERT_SHADOW(depending on what was replaced).
                    _stagedMutations.Add(new StagedMutation(doc, contentBytes, StagedMutationType.Insert, updatedDoc));
                }
                else
                {
                    // If doc is already in stagedMutations as a REPLACE, then overwrite it.
                    _stagedMutations.Add(new StagedMutation(doc, contentBytes, StagedMutationType.Replace, updatedDoc));
                }

                return TransactionGetResult.FromInsert(
                    doc.Collection,
                    doc.Id,
                    contentBytes,
                    _overallContext.TransactionId,
                    _attemptId,
                    _atrId,
                    _atrBucketName,
                    _atrScopeName,
                    _atrCollectionName,
                    updatedDoc,
                    _transcoder);
            }
            catch (Exception e)
            {
                // TODO: Error-handling per spec.
                throw;
            }
        }

        private List<MutateInSpec> CreateMutationOps( string op)
        {
            var specs = new List<MutateInSpec>
            {
                MutateInSpec.Upsert(TransactionFields.TransactionId, _overallContext.TransactionId,
                    createPath: true, isXattr: true),
                MutateInSpec.Upsert(TransactionFields.AttemptId, _attemptId, createPath: true, isXattr: true),
                MutateInSpec.Upsert(TransactionFields.AtrId, _atrId, createPath: true, isXattr: true),
                MutateInSpec.Upsert(TransactionFields.AtrBucketName, _atrBucketName, isXattr: true),
                MutateInSpec.Upsert(TransactionFields.AtrCollName, _atrLongCollectionName, isXattr: true),
                MutateInSpec.Upsert(TransactionFields.Type, op, createPath: true, isXattr: true),
            };

            return specs;
        }

        public async Task<TransactionGetResult> Insert(ICouchbaseCollection collection, string id, object content)
        {
            DoneCheck();
            CheckExpiry();
            InitAtrIfNeeded(collection, id);
            if (_stagedMutations.Count == 0)
            {
                var setAtrPendingResult = await SetAtrPending(collection);
            }

            // If this document already exists in StagedMutation, raise Error(FAIL_OTHER, cause=IllegalStateException [or platform-specific equivalent]). 
            if (_stagedMutations.Any(sm => sm.Doc.Id == id))
            {
                throw CreateError(this, ErrorClass.FailOther)
                    .Cause(new InvalidOperationException("Document is already staged for insert."))
                    .Build();
            }

            return await CreateStagedInsert(collection, id, content).CAF();
        }

        private async Task<TransactionGetResult> CreateStagedInsert(ICouchbaseCollection collection, string id, object content, ulong? cas = null)
        {
            try
            {
                // Check expiration again, since insert might be retried.
                CheckExpiry();

                await _testHooks.BeforeStagedInsert(this, id).CAF();

                var contentBytes = GetContentBytes(content);
                List<MutateInSpec> specs = CreateMutationOps("insert");
                specs.Add(MutateInSpec.Upsert(TransactionFields.StagedData, contentBytes, isXattr: true));

                var mutateResult = await collection.MutateInAsync(id, specs, opts =>
                    {
                        if (cas.HasValue)
                        {
                            opts.Cas(cas.Value).StoreSemantics(StoreSemantics.Replace);
                        }
                        else
                        {
                            opts.StoreSemantics(StoreSemantics.Insert);
                        }

                        opts.Durability(_effectiveDurabilityLevel);

                        // TODO: accessDeleted = true (needs NCBC-2573)
                        // TODO: createAsDeleted = true (needs NCBC-2573)
                    }).CAF();

                var getResult = TransactionGetResult.FromInsert(
                    collection,
                    id,
                    contentBytes,
                    _overallContext.TransactionId,
                    _attemptId,
                    _atrId,
                    _atrBucketName,
                    _atrScopeName,
                    _atrCollectionName,
                    mutateResult,
                    _transcoder);

                await _testHooks.AfterStagedInsertComplete(this, id).CAF();

                var stagedMutation = new StagedMutation(getResult, contentBytes, StagedMutationType.Insert, mutateResult);
                _stagedMutations.Add(stagedMutation);

                return getResult;
            }
            catch (FeatureNotAvailableException e)
            {
                throw CreateError(this, ErrorClass.FailOther)
                    .Cause(e).Build();
            }
            catch (ErrorWrapperException e)
            {
                // TODO: on error, classify error per spec 
                throw;
            }
        }

        private async Task<IMutateInResult> SetAtrPending(ICouchbaseCollection collection)
        {
            _atrId = _atrId ?? throw new InvalidOperationException("atrId is not present");

            try
            {
                await _testHooks.BeforeAtrPending(this);

                var prefix = $"attempts.{_attemptId}";
                var t1 = _overallContext.StartTime;
                var t2 = DateTimeOffset.UtcNow;
                var tElapsed = t2 - t1;
                var tc = _config.ExpirationTime;
                var tRemaining = tc - tElapsed;
                var exp = (ulong)Math.Max(Math.Min(tRemaining.TotalMilliseconds, tc.TotalMilliseconds), 0);

                var mutateInResult = await collection.MutateInAsync(_atrId, specs =>
                        specs.Insert($"{prefix}.{TransactionFields.AtrFieldTransactionId}", _overallContext.TransactionId,
                                createPath: true, isXattr: true)
                            .Insert($"{prefix}.{TransactionFields.AtrFieldStatus}", AttemptStates.Pending.FullName(), createPath: false, isXattr: true)
                            .Insert($"{prefix}.{TransactionFields.AtrFieldStartTimestamp}", MutationMacro.Cas)
                            .Insert($"{prefix}.{TransactionFields.AtrFieldExpiresAfterMsecs}", exp, createPath: false, isXattr: true),
                    opts => opts.StoreSemantics(StoreSemantics.Upsert)
                ).CAF();

                var lookupInResult = await collection.LookupInAsync(_atrId,
                    specs => specs.Get($"{prefix}.{TransactionFields.AtrFieldStartTimestamp}", isXattr: true));
                var fetchedCas = lookupInResult.ContentAs<string>(0);
                var getResult = await collection.GetAsync(_atrId).CAF();
                var atr = getResult.ContentAs<dynamic>();
                await _testHooks.AfterAtrPending(this);
                return mutateInResult;
            }
            catch (Exception e)
            {
                // TODO: Handle error per spec
                throw;
            }
        }

        private byte[] GetContentBytes(object content)
        {
            using var memoryStream = new MemoryStream();
            var flags = _transcoder.GetFormat(content);
            _transcoder.Encode(memoryStream, content, flags, OpCode.Add);
            var contentBytes = memoryStream.GetBuffer();
            return contentBytes;
        }

        public async Task Remove(TransactionGetResult doc)
        {
            DoneCheck();
            CheckExpiry();
            CheckWriteWriteConflict(doc);
            if (_stagedMutations.Count == 0)
            {
                var setAtrPendingResult = await SetAtrPending(doc.Collection);
            }

            await CreateStagedRemove(doc);
        }

        private async Task CreateStagedRemove(TransactionGetResult doc)
        {
            try
            {
                await _testHooks.BeforeStagedRemove(this, doc.Id).CAF();
                var specs = CreateMutationOps(op: "remove");
                specs.Add(MutateInSpec.Upsert(TransactionFields.StagedData, TransactionFields.StagedDataRemoveKeyword, isXattr: true));

                if (doc.DocumentMetadata != null)
                {
                    var dm = doc.DocumentMetadata;
                    if (dm.Cas != null)
                    {
                        specs.Add(MutateInSpec.Upsert(TransactionFields.PreTxnCas, dm.Cas, isXattr: true));
                    }

                    if (dm.RevId != null)
                    {
                        specs.Add(MutateInSpec.Upsert(TransactionFields.PreTxnRevid, dm.RevId, isXattr: true));
                    }

                    if (dm.ExpTime != null)
                    {
                        specs.Add(MutateInSpec.Upsert(TransactionFields.PreTxnExptime, dm.ExpTime, isXattr: true));
                    }
                }

                var opts = new MutateInOptions()
                    .Cas(doc.Cas)
                    .StoreSemantics(StoreSemantics.Replace)
                    .Durability(_effectiveDurabilityLevel);

                var updatedDoc = await doc.Collection.MutateInAsync(doc.Id, specs, opts).CAF();
                await _testHooks.AfterStagedRemoveComplete(this, doc.Id).CAF();

                doc.Cas = updatedDoc.Cas;
                if (_stagedMutations.Exists(sm => sm.Doc.Id == doc.Id && sm.Type == StagedMutationType.Insert))
                {
                    // TXNJ-35: handle insert-delete with same doc

                    // Commit+rollback: Want to delete the staged empty doc
                    // However this is hard in practice.  If we remove from stagedInsert and add to
                    // stagedRemove then commit will work fine, but rollback will not remove the doc.
                    // So, fast fail this scenario.
                    throw new InvalidOperationException($"doc {_redactor.UserData(doc.Id)} is being removed after being inserted in the same txn.");
                }

                var stagedRemove = new StagedMutation(doc, null, StagedMutationType.Remove, updatedDoc);
                _stagedMutations.Add(stagedRemove);
            }
            catch (Exception e)
            {
                // TODO: Handle errors per spec.
                throw;
            }
        }

        public async Task Commit()
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#Commit
            CheckExpiry();
            DoneCheck();
            lock (_initAtrLock)
            {
                if (_atrId == null)
                {
                    // If no ATR has been selected then no mutation has been performed. Return success.
                    // This will leave state as NOTHING_WRITTEN,
                    return;
                }
            }

            try
            {
                await SetAtrCommit().CAF();
                await UnstageDocs().CAF();
                await SetAtrComplete().CAF();
            }
            catch (Exception e)
            {
                throw;
            }

        }

        private async Task SetAtrComplete()
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#SetATRComplete
            try
            {
                await _testHooks.BeforeAtrComplete(this).CAF();
                var prefix = $"attempts.{_attemptId}";
                var specs = new MutateInSpec[]
                {
                    MutateInSpec.Upsert($"{prefix}.{TransactionFields.AtrFieldStatus}", AttemptStates.Completed.FullName(), isXattr: true),
                    MutateInSpec.Upsert($"{prefix}.{TransactionFields.AtrFieldTimestampComplete}", MutationMacro.Cas)
                };

                var mutateResult = await _atrCollection
                    .MutateInAsync(_atrId, specs, opts => opts.StoreSemantics(StoreSemantics.Replace)).CAF();

                await _testHooks.AfterAtrComplete(this).CAF();
                _state = AttemptStates.Completed;
            }
            catch (Exception e)
            {
                /*
                 * On error err (from any of the preceding items in this section), classify as error class ec then:
FAIL_HARD -> Error(ec, err, rollback=false)
Else -> Setting the ATR to COMPLETED is purely a cleanup step, there’s no need to retry it until expiry. Simply return success (leaving state at COMMITTED).
Note this will leave TransactionResult::unstageCompleted() returning false, even though a use of TransactionResult::mutationState() would be fine. Fixing this issue would require the complexity of maintaining additional mutable state. We will monitor if this is a problem in real deployments and can consider returning to this.
A FAIL_AMBIGUOUS could leave the ATR state as COMPLETED but the in-memory state as COMMITTED. This shouldn’t cause any problems.
                 */

                throw;
            }
        }

        private async Task UnstageDocs()
        {
            foreach (var sm in _stagedMutations)
            {
                switch (sm.Type)
                {
                    case StagedMutationType.Remove:
                        await UnstageRemove(sm, ambiguityResolutionMode: false).CAF();
                        break;
                    case StagedMutationType.Insert:
                        await UnstageMutation(sm, casZeroMode: false, insertMode: true, ambiguityResolutionMode: false).CAF();
                        break;
                    case StagedMutationType.Replace:
                        await UnstageMutation(sm, casZeroMode: false, insertMode: false, ambiguityResolutionMode: false)
                            .CAF();
                        break;
                    default:
                        throw new InvalidOperationException($"Cannot un-stage transaction mutation of type {sm.Type}");
                }
            }
        }

        private async Task UnstageRemove(StagedMutation sm, bool ambiguityResolutionMode)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#Unstaging-Removes
            await _testHooks.BeforeDocRemoved(this, sm.Doc.Id);
            if (_overallContext.IsExpired)
            {
                // TODO: set ExpirationOverTimeMode
            }

            try
            {

            }
            catch (Exception e)
            {
                /*
                 * On error err (from any of the preceding items in this section), classify as error class ec then:
If in ExpiryOvertimeMode -> Error(ec, AttemptExpired(err), rollback=false, raise=TRANSACTION_FAILED_POST_COMMIT)
Else FAIL_AMBIGUOUS -> Retry this section from the top, after OpRetryDelay, with ambiguityResolutionMode=true.
Else FAIL_DOC_NOT_FOUND -> Doc was removed in-between stage and unstaging.
Either we are in in ambiguityResolutionMode mode, and our second attempt found that the first attempt was successful. Or, another actor has removed the document. Either way, we cannot continue as the returned mutationTokens won’t contain this removal. Raise Error(ec, err, rollback=false, raise=TRANSACTION_FAILED_POST_COMMIT).
Note: The ambiguityResolutionMode logic is redundant currently. But we may have better handling for this in the future, e.g. a mode where the application can specify that mutationTokens isn’t important for this transaction.
Else FAIL_HARD -> Error(ec, err, rollback=false)
Else -> Raise Error(ec, cause=err, rollback=false, raise=TRANSACTION_FAILED_POST_COMMIT). This will result in success being return to the application, but result.unstagingCompleted() will be false. See “Unstaging Inserts & Replaces” for the logic behind this.
                 */
                throw;
            }
        }

        private async Task UnstageMutation(StagedMutation sm, bool casZeroMode = false, bool insertMode = false, bool ambiguityResolutionMode = false)
        {
            // https://hackmd.io/Eaf20XhtRhi8aGEn_xIH8A#Unstaging-Inserts-and-Replaces-Protocol-20-version
            await _testHooks.BeforeDocCommitted(this, sm.Doc.Id).CAF();
            if (_overallContext.IsExpired)
            {
                // TODO: set ExpirationOverTimeMode
            }

            try
            {
                IMutationResult mutateResult;
                if (insertMode)
                {
                    mutateResult = await sm.Doc.Collection.InsertAsync(sm.Doc.Id, sm.Content).CAF();
                }
                else
                {
                    mutateResult = await sm.Doc.Collection.MutateInAsync(sm.Doc.Id, specs =>
                                specs
                                    ////.Upsert(TransactionFields.TransactionInterfacePrefixOnly, (string?)null,
                                    ////    isXattr: true)
                                    .Remove(TransactionFields.TransactionInterfacePrefixOnly, isXattr: true)
                                    .SetDoc(sm.Content),
                            opts => opts.Cas( casZeroMode ? 0 : sm.Doc.Cas))
                        .CAF();

                }

                if (mutateResult?.MutationToken != null)
                {
                    // TODO: the java client uses the doc.id, here, but is that sufficient?  Can't we have multiple docs with the same id from different collections?
                    _finalMutations.Add(sm.Doc.Id, mutateResult.MutationToken);
                }

                await _testHooks.AfterDocCommittedBeforeSavingCas(this, sm.Doc.Id);
            }
            catch (Exception e)
            {
                /* On error err (from any of the preceding items in this section), classify as error class ec then:
If in ExpiryOvertimeMode -> Error(ec, AttemptExpired(err), rollback=false, raise=TRANSACTION_FAILED_POST_COMMIT)
Else FAIL_AMBIGUOUS -> Retry this section from the top, after OpRetryDelay, with ambiguityResolutionMode=true and the current casZero.
Else FAIL_CAS_MISMATCH -> Doc has changed in-between stage and unstaging.
If ambiguityResolutionMode=true, then our previous attempt likely succeeded. Unfortunately, we cannot continue as our returned mutationTokens wouldn’t include this mutation. Raise Error(ec, err, rollback=false, raise=TRANSACTION_FAILED_POST_COMMIT).
Else, another actor has changed the document, breaking the co-operative model (it should not be possible for another transaction to do this). Publish an IllegalDocumentState event to the application (the details of event publishing are platform-specific: on Java, it uses the Java SDK’s event bus). Run this section again from the top, with casZeroMode=true and the current ambiguityResolutionMode and insertMode.
Else FAIL_DOC_NOT_FOUND -> Doc was removed in-between stage and unstaging.
Something has broken the co-operative model. The transaction must commit, so we will insert the document.
Publish an IllegalDocumentState event to the application.
Run this section again from the top, after OpRetryDelay, with insertMode=true, and the current ambiguityResolutionMode and casZeroMode. This will insert the document.
Else FAIL_HARD -> Error(ec, err, rollback=false)
Else FAIL_DOC_ALREADY_EXISTS ->
If in ambiguityResolutionMode, probably we inserted a doc over a shadow document, and it raised FAIL_AMBIGUOUS but was successful. Resolving this is perhaps impossible - we don’t want to retry the operation, as the document is now committed and another transaction (or KV op) may have started on it. We could reread the doc and check it contains the expected content - but again it may have been involved in another transaction. So, raise Error(ec, cause=err, rollback=false, raise=TRANSACTION_FAILED_POST_COMMIT).
Else, seems the co-operative model has been broken. The transaction must commit, so we will replace the document.
Publish an IllegalDocumentState event to the application.
Run this section again from the top, after OpRetryDelay, with insertMode=false, the current ambiguityResolutionMode, and casZeroMode=true.
Else -> Raise Error(ec, cause=err, rollback=false, raise=TRANSACTION_FAILED_POST_COMMIT). This will result in success being return to the application, but result.unstagingCompleted() will be false.
The transaction is conceptually committed but unstaging could not complete. Since we have reached the COMMIT point, all transactions will see the post-transaction values now. For many application cases (e.g. those not doing RYOWs) this is not an error at all, and it seems unwise to repeatedly retry this operation if the cluster is currently overwhelmed. Returning success now will make transactions more resilient in the face of e.g. rebalances. The commit will still be completed by the async cleanup process later.
                */
                throw;
            }
        }

        private async Task SetAtrCommit(bool ambiguityResolutionMode = false)
        {
            try
            {
                CheckExpiry();
                await _testHooks.BeforeAtrCommit(this).CAF();
                var prefix = $"attempts.{_attemptId}";
                var stagedInserts = _stagedMutations.Where(sm => sm.Type == StagedMutationType.Insert);
                var stagedReplaces = _stagedMutations.Where(sm => sm.Type == StagedMutationType.Replace);
                var stagedRemoves = _stagedMutations.Where(sm => sm.Type == StagedMutationType.Remove);

                var inserts = new JArray(stagedInserts.Select(sm => sm.ForAtr()));
                var replaces = new JArray(stagedReplaces.Select(sm => sm.ForAtr()));
                var removes = new JArray(stagedRemoves.Select(sm => sm.ForAtr()));
                var specs = new MutateInSpec[]
                { 
                    MutateInSpec.Upsert($"{prefix}.{TransactionFields.AtrFieldStatus}", AttemptStates.Committed.FullName(), isXattr: true),
                    MutateInSpec.Upsert($"{prefix}.{TransactionFields.AtrFieldStartCommit}", MutationMacro.Cas), 
                    MutateInSpec.Upsert($"{prefix}.{TransactionFields.AtrFieldDocsInserted}", inserts, isXattr: true),
                    MutateInSpec.Upsert($"{prefix}.{TransactionFields.AtrFieldDocsReplaced}", replaces, isXattr: true),
                    MutateInSpec.Upsert($"{prefix}.{TransactionFields.AtrFieldDocsRemoved}", removes, isXattr: true),
                    MutateInSpec.Upsert($"{prefix}.{TransactionFields.AtrFieldPendingSentinel}", 0, isXattr: true)
                };


                var mutateInResult = await _atrCollection
                    .MutateInAsync(_atrId, specs, opts => opts.StoreSemantics(StoreSemantics.Replace)).CAF();

                await _testHooks.AfterAtrCommit(this).CAF();
                _state = AttemptStates.Committed;
            }
            catch (Exception e)
            {
                /*
                 * On error err (from any of the preceding items in this section), classify as error class ec then:
FAIL_EXPIRY ->
Protocol 1:
If ambiguityResolutionMode==true, we were unable to attain clarity over whether we reached committed or not. Set ExpiryOvertimeMode and raise Error(ec, AttemptExpired(err), raise=TRANSACTION_COMMIT_AMBIGUOUS)
Else, we unambiguously were not able to set the ATR to Committed. Set ExpiryOvertimeMode and raise Error(ec, AttemptExpired(err), raise=TRANSACTION_EXPIRED)
Protocol 2:
We unambiguously were not able to set the ATR to Committed. Set ExpiryOvertimeMode and raise Error(ec, AttemptExpired(err), raise=TRANSACTION_EXPIRED)
Else FAIL_AMBIGUOUS ->
Ambiguity resolution is very important here, and we cannot proceed until we are certain. E.g. if the op succeeded then we are past the point of no return and must commit.
Protocol 1:
Repeat the SetATRCommit step from the top to retry the idempotent commit step, with ambiguityResolutionMode=true, after waiting OpRetryDelay.
Protocol 2:
Perform the SetATRCommit Ambiguity Resolution logic.
Else FAIL_HARD -> Error(ec, err, rollback=false)
Else FAIL_TRANSIENT -> Error(ec, err, retry=true)
Else -> Error(ec, err)
                 */
                throw;
            }
        }

        public Task Rollback() => throw new NotImplementedException();
        public Task Defer() => throw new NotImplementedException();

        protected bool IsDone => _state != AttemptStates.NothingWritten && _state != AttemptStates.Pending;

        protected void DoneCheck()
        {
            if (IsDone)
            {
                throw CreateError(this, ErrorClass.FailOther)
                    .Cause(new InvalidOperationException("Cannot perform operations after a transaction has been committed or rolled back."))
                    .DoNotRollbackAttempt()
                    .Build();
            }
        }

        protected void InitAtrIfNeeded(ICouchbaseCollection collection, string id)
        {
            lock (_initAtrLock)
            {
                if (_atrId == null)
                {
                    _atrId = AtrIds.GetAtrId(id);
                    _atrCollection = collection;
                    _atrCollectionName = collection.Name; 
                    _atrScopeName = collection.Scope.Name;
                    _atrBucketName = collection.Scope.Bucket.Name;
                    _atrLongCollectionName = _atrScopeName + "." + _atrCollectionName;

                    // TODO:  check consistency on use of _atrCollectionName vs. _atrLongCollectionName
                    ////LOGGER.info(attemptId, "First mutated doc in txn is '%s' on vbucket %d, so using atr %s",
                    ////    RedactableArgument.redactUser(id), vbucketIdForDoc, atr);
                }
            }
        }

        protected void CheckExpiry()
        {
            // TODO:  Set and handle ExpirationOvertimeMode
            if (_overallContext.IsExpired)
            {
                throw CreateError(this, ErrorClass.FailExpiry)
                    .RaiseException(ErrorWrapperException.FinalErrorToRaise.TransactionExpired)
                    .Build();
            }
        }

        protected void CheckWriteWriteConflict(TransactionGetResult gr)
        {
            // TODO: implement, see checkATREntryForBlockingDocInternal (AttemptContextReactive.java:704)) for reference
            /*
             * CheckWriteWriteConflict
This logic checks and handles a document X previously read inside a transaction, A, being involved in another transaction B. It takes a TransactionGetResult gr variable.

If gr has no transaction Metadata, it’s fine to proceed.
Else, if transaction A == transaction B, it’s fine to proceed
Else there’s a write-write conflict.
Note that it’s essential not to get blocked permanently here. B could be a crashed pending transaction, in which case X will never be cleaned up. The basic algo is to check B’s ATR entry - if it’s been cleaned up (removed), we can proceed.
But, most of the time, B just needs a little time to complete, so there’s no need to check the ATR, it’s just adding unnecessary operations.
So there’s a threshold after which we start checking B’s ATR. This is arbitrarily set at one second. This is just measured from the start of the transaction (we don’t try and measure separately times spent blocked by multiple transactions). This is not currently configurable.
The algo:
If under the threshold, raise Error(FAIL_WRITE_WRITE_CONFLICT, DocumentAlreadyInTransaction, retry=true)
Else get B’s ATR entry:
Call hook beforeCheckATREntryForBlockingDoc, passing this AttemptContext and the ATR’s key. On error from this
Do a lookupIn call to fetch the ATR entry.
If the ATR exists but the entry does not, it has been cleaned up. Proceed.
Else, including on any error, assume we’re still blocked (CP) and raise
             */
        }

        private enum StagedMutationType
        {
            Undefined = 0,
            Insert = 1,
            Remove = 2,
            Replace = 3
        }

        private class StagedMutation
        {
            public TransactionGetResult Doc { get; }
            public byte[] Content { get; }
            public StagedMutationType Type { get; }
            public IMutationResult MutationResult { get; }

            public StagedMutation(TransactionGetResult doc, byte[] content, StagedMutationType type, IMutationResult mutationResult)
            {
                Doc = doc;
                Content = content;
                Type = type;
                MutationResult = mutationResult;
            }

            public JObject ForAtr() => new JObject(
                    new JProperty(TransactionFields.AtrFieldPerDocId, Doc.Id),
                    new JProperty(TransactionFields.AtrFieldPerDocBucket, Doc.Collection.Scope.Bucket.Name),
                    new JProperty(TransactionFields.AtrFieldPerDocScope, Doc.Collection.Scope.Name),
                    new JProperty(TransactionFields.AtrFieldPerDocCollection, Doc.Collection.Name)
                );

        }
    }
}
