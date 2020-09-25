﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Couchbase.Transactions.Error.Internal;

namespace Couchbase.Transactions.Error
{
    internal class ErrorBuilder
    {
        private readonly AttemptContext _ctx;
        private readonly ErrorClass _causingErrorClass;
        private ErrorWrapperException.FinalError _toRaise = ErrorWrapperException.FinalError.TransactionFailed;
        private bool _rollbackAttempt;
        private bool _retryTransaction;
        private Exception _cause = new Exception("generic exception cause");

        private ErrorBuilder(AttemptContext ctx, ErrorClass causingErrorClass)
        {
            _ctx = ctx;
            _causingErrorClass = causingErrorClass;
        }

        public static ErrorBuilder CreateError(AttemptContext ctx, ErrorClass causingErrorClass)
        {
            return new ErrorBuilder(ctx, causingErrorClass);
        }

        public ErrorBuilder RaiseException(ErrorWrapperException.FinalError finalErrorToRaise)
        {
            _toRaise = finalErrorToRaise;
            return this;
        }

        public ErrorBuilder DoNotRollbackAttempt()
        {
            _rollbackAttempt = false;
            return this;
        }

        public ErrorBuilder RetryTransaction()
        {
            _retryTransaction = true;
            return this;
        }

        public ErrorBuilder Cause(Exception cause)
        {
            if (cause.StackTrace != null)
            {
                _cause = cause;
                return this;
            }

            // capture the stack trace
            try
            {
                throw cause;
            }
            catch (Exception causeWithStackTrace)
            {
                _cause = causeWithStackTrace;
            }

            return this;
        }

        public ErrorWrapperException Build() => new ErrorWrapperException(_ctx, _causingErrorClass, _rollbackAttempt, _retryTransaction, _cause, _toRaise);
    }
}