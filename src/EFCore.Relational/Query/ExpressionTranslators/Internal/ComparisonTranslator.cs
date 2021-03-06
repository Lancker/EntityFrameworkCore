// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Query.Expressions;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore.Query.ExpressionTranslators.Internal
{
    /// <summary>
    ///     <para>
    ///         This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///         directly from your code. This API may change or be removed in future releases.
    ///     </para>
    ///     <para>
    ///         The service lifetime is <see cref="ServiceLifetime.Singleton"/>. This means a single instance
    ///         is used by many <see cref="DbContext"/> instances. The implementation must be thread-safe.
    ///         This service cannot depend on services registered as <see cref="ServiceLifetime.Scoped"/>.
    ///     </para>
    /// </summary>
    public class ComparisonTranslator : IExpressionFragmentTranslator
    {
        private static readonly Dictionary<ExpressionType, ExpressionType> _operatorMap = new Dictionary<ExpressionType, ExpressionType>
        {
            { ExpressionType.LessThan, ExpressionType.GreaterThan },
            { ExpressionType.LessThanOrEqual, ExpressionType.GreaterThanOrEqual },
            { ExpressionType.GreaterThan, ExpressionType.LessThan },
            { ExpressionType.GreaterThanOrEqual, ExpressionType.LessThanOrEqual },
            { ExpressionType.Equal, ExpressionType.Equal },
            { ExpressionType.NotEqual, ExpressionType.NotEqual }
        };

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual Expression Translate(Expression expression)
        {
            if (expression is BinaryExpression binaryExpression)
            {
                if (!_operatorMap.ContainsKey(expression.NodeType))
                {
                    return null;
                }

                var leftMethodCall = RemoveNullConditional(binaryExpression.Left) as MethodCallExpression;
                var rightConstant = binaryExpression.Right.RemoveConvert() as ConstantExpression;
                var translated = TranslateInternal(t => t, expression.NodeType, leftMethodCall, rightConstant);
                if (translated != null)
                {
                    return translated;
                }

                var leftConstant = binaryExpression.Left.RemoveConvert() as ConstantExpression;
                var rightMethodCall = RemoveNullConditional(binaryExpression.Right) as MethodCallExpression;
                var translatedReverse = TranslateInternal(t => _operatorMap[t], expression.NodeType, rightMethodCall, leftConstant);

                return translatedReverse;
            }

            return null;
        }

        private static Expression RemoveNullConditional(Expression expression)
            => expression.RemoveConvert() is NullConditionalExpression nullConditionalExpression
                ? RemoveNullConditional(nullConditionalExpression.AccessOperation)
                : expression;

        private static Expression TranslateInternal(
            Func<ExpressionType, ExpressionType> opFunc,
            ExpressionType op,
            MethodCallExpression methodCall,
            ConstantExpression constant)
        {
            if (methodCall != null
                && methodCall.Type == typeof(int)
                && constant != null
                && constant.Type == typeof(int))
            {
                var constantValue = (int)constant.Value;
                Expression left = null, right = null;

                if (methodCall.Method.Name == "Compare"
                    && methodCall.Arguments.Count == 2
                    && methodCall.Arguments[0].Type == methodCall.Arguments[1].Type)
                {
                    left = methodCall.Arguments[0];
                    right = methodCall.Arguments[1];
                }
                else if (methodCall.Method.Name == "CompareTo"
                         && methodCall.Arguments.Count == 1
                         && methodCall.Object != null
                         && methodCall.Object.Type == methodCall.Arguments[0].Type)
                {
                    left = methodCall.Object;
                    right = methodCall.Arguments[0];
                }

                if (left != null)
                {
                    if (constantValue == 0)
                    {
                        // Compare(strA, strB) > 0 => strA > strB
                        return new ComparisonExpression(opFunc(op), left, right);
                    }

                    if (constantValue == 1)
                    {
                        if (op == ExpressionType.Equal)
                        {
                            // Compare(strA, strB) == 1 => strA > strB
                            return new ComparisonExpression(ExpressionType.GreaterThan, left, right);
                        }

                        if (op == opFunc(ExpressionType.LessThan))
                        {
                            // Compare(strA, strB) < 1 => strA <= strB
                            return new ComparisonExpression(ExpressionType.LessThanOrEqual, left, right);
                        }
                    }

                    if (constantValue == -1)
                    {
                        if (op == ExpressionType.Equal)
                        {
                            // Compare(strA, strB) == -1 => strA < strB
                            return new ComparisonExpression(ExpressionType.LessThan, left, right);
                        }

                        if (op == opFunc(ExpressionType.GreaterThan))
                        {
                            // Compare(strA, strB) > -1 => strA >= strB
                            return new ComparisonExpression(ExpressionType.GreaterThanOrEqual, left, right);
                        }
                    }
                }
            }

            return null;
        }
    }
}
