﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.Tasks;

namespace Couchbase.Transactions.Internal.Test
{
    /// <summary>
    /// Protected hooks purely for testing purposes.  If you're an end-user looking at these for any reason, then
    /// please contact us first about your use-case: we are always open to adding good ideas into the transactions
    /// library.
    /// </summary>
    /// <remarks>All methods have default no-op implementations.</remarks>
    public interface ITestHooks
    {
        Task<int?> BeforeAtrCommit(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> AfterAtrCommit(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> BeforeDocCommitted(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> BeforeDocRolledBack(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> AfterDocCommittedBeforeSavingCas(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> AfterDocCommitted(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> AfterDocsCommitted(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> BeforeDocRemoved(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> AfterDocRemovedPreRetry(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> AfterDocRemovedPostRetry(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> AfterDocsRemoved(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> BeforeAtrPending(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> AfterAtrPending(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> AfterAtrComplete(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> BeforeAtrComplete(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> BeforeAtrRolledBack(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> AfterGetComplete(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> BeforeRollbackDeleteInserted(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> AfterStagedReplaceComplete(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> AfterStagedRemoveComplete(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> BeforeStagedInsert(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> BeforeStagedRemove(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> BeforeStagedReplace(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> AfterStagedInsertComplete(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> BeforeGetAtrForAbort(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> BeforeAtrAborted(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> AfterAtrAborted(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> AfterAtrRolledBack(AttemptContext self) => Task.FromResult<int?>(0);

        Task<int?> AfterRollbackReplaceOrRemove(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> AfterRollbackDeleteInserted(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> BeforeRemovingDocDuringStagedInsert(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> BeforeCheckAtrEntryForBlockingDoc(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> BeforeDocGet(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<int?> BeforeGetDocInExistsDuringStagedInsert(AttemptContext self, string id) => Task.FromResult<int?>(0);

        Task<bool> HasExpiredClientSideHook(AttemptContext self, string place, [AllowNull] string docId) => Task.FromResult(false);
        Task<int?> BeforeAtrCommitAmiguityResolution(AttemptContext attemptContext) => Task.FromResult<int?>(0);
    }

    /// <summary>
    /// Implementation of ITestHooks that relies on default interface implementation.
    /// </summary>
    internal class DefaultTestHooks : ITestHooks
    {
        public static readonly ITestHooks Instance = new DefaultTestHooks();
    }
}
