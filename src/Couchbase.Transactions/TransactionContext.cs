﻿using System;
using System.Collections.Generic;

namespace Couchbase.Transactions
{
    internal class TransactionContext
    {
        public string TransactionId { get; } = Guid.NewGuid().ToString();
        public IReadOnlyCollection<ITransactionAttempt> Attempts = new List<ITransactionAttempt>();
    }
}
