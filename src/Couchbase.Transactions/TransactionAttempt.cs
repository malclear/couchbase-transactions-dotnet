﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Couchbase.Core;
using Couchbase.KeyValue;
using Couchbase.Transactions.Log;
using Couchbase.Transactions.Support;
using Newtonsoft.Json;

namespace Couchbase.Transactions
{
    public class TransactionAttempt
    {
        public TimeSpan TimeTaken { get; internal set; }

        [JsonIgnore]
        public ICouchbaseCollection? AtrCollection { get; internal set; }

        public string? AtrCollectionName => AtrCollection?.Name;
        public string? AtrScopeName => AtrCollection?.Scope?.Name;
        public string? AtrBucketName => AtrCollection?.Scope?.Bucket?.Name;

        public string? AtrId { get; internal set; }
        public AttemptStates FinalState { get; internal set; }
        public string AttemptId { get; internal set; } = string.Empty;
        public IEnumerable<string> StagedInsertedIds { get; internal set; } = Enumerable.Empty<string>();
        public IEnumerable<string> StagedReplaceIds { get; internal set; } = Enumerable.Empty<string>();
        public IEnumerable<string> StagedRemoveIds { get; internal set; } = Enumerable.Empty<string>();
        public Exception? TermindatedByException { get; internal set; } = null;
        public IEnumerable<MutationToken> MutationTokens { get; internal set; } = Enumerable.Empty<MutationToken>();

        public override string ToString()
        {
            const int logDeferCharsToLog = 5; // TODO: move constant to equivalent of LogDefer class in Java code.
            var sb = new StringBuilder();
            sb.Append(nameof(TransactionAttempt)).Append("{id=").Append(AttemptId.SafeSubstring(logDeferCharsToLog));
            sb.Append(",state=").Append(FinalState);
            sb.Append(",atrColl=").Append(AtrCollection?.Name ?? "<none>");
            sb.Append("/atrId=").Append(AtrId ?? "<none>");
            sb.Append("}");
            return sb.ToString();
        }
    }
}
