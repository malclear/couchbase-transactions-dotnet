﻿using System;
using System.Collections.Generic;
using System.Text;
using Couchbase.Core.Retry;

namespace Couchbase.Transactions.Error.Attempts
{
    public class DocumentAlreadyInTransactionException : AttemptException, IRetryable
    {
        public TransactionGetResult Doc { get; }

        private DocumentAlreadyInTransactionException(AttemptContext ctx, TransactionGetResult doc, string msg)
            : base(ctx, msg)
        {
            Doc = doc;
        }

        public static DocumentAlreadyInTransactionException Create(AttemptContext ctx, TransactionGetResult doc)
        {
            var msg =
                $"Document {ctx.Redactor.UserData(doc.Id)} is already in a transaction, atr={doc.TransactionXattrs?.AtrRef?.ToString()}, attemptId = {doc.TransactionXattrs?.Id?.AttemptId ?? "-"}";

            return new DocumentAlreadyInTransactionException(ctx, doc, msg);
        }
    }
}


/* ************************************************************
 *
 *    @author Couchbase <info@couchbase.com>
 *    @copyright 2021 Couchbase, Inc.
 *
 *    Licensed under the Apache License, Version 2.0 (the "License");
 *    you may not use this file except in compliance with the License.
 *    You may obtain a copy of the License at
 *
 *        http://www.apache.org/licenses/LICENSE-2.0
 *
 *    Unless required by applicable law or agreed to in writing, software
 *    distributed under the License is distributed on an "AS IS" BASIS,
 *    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *    See the License for the specific language governing permissions and
 *    limitations under the License.
 *
 * ************************************************************/
