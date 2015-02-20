﻿' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.Collections.Immutable
Imports System.Runtime.InteropServices
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.VisualBasic.EditAndContinue
    Friend NotInheritable Class SyntaxUtilities
        <Conditional("DEBUG")>
        Public Shared Sub AssertIsBody(syntax As SyntaxNode, allowLambda As Boolean)
            ' lambda/query
            If IsLambdaBody(syntax) Then
                Debug.Assert(allowLambda)
                Debug.Assert(TypeOf syntax Is ExpressionSyntax OrElse TypeOf syntax Is LambdaHeaderSyntax)
                Return
            End If

            ' sub/function/ctor/operator/accessor
            If TypeOf syntax Is MethodBlockBaseSyntax Then
                Return
            End If

            ' field/property initializer
            If TypeOf syntax Is ExpressionSyntax Then
                If syntax.Parent.Kind = SyntaxKind.EqualsValue Then
                    If syntax.Parent.Parent.IsKind(SyntaxKind.PropertyStatement) Then
                        Return
                    End If

                    Debug.Assert(syntax.Parent.Parent.IsKind(SyntaxKind.VariableDeclarator))
                    Debug.Assert(syntax.Parent.Parent.Parent.IsKind(SyntaxKind.FieldDeclaration))
                    Return
                ElseIf syntax.Parent.Kind = SyntaxKind.AsNewClause Then
                    If syntax.Parent.Parent.IsKind(SyntaxKind.PropertyStatement) Then
                        Return
                    End If

                    Debug.Assert(syntax.Parent.Parent.IsKind(SyntaxKind.VariableDeclarator))
                    Debug.Assert(syntax.Parent.Parent.Parent.IsKind(SyntaxKind.FieldDeclaration))
                    Return
                End If
            End If

            ' field array initializer
            If TypeOf syntax Is ArgumentListSyntax Then
                Debug.Assert(syntax.Parent.IsKind(SyntaxKind.ModifiedIdentifier))
                Debug.Assert(syntax.Parent.Parent.IsKind(SyntaxKind.VariableDeclarator))
                Debug.Assert(syntax.Parent.Parent.Parent.IsKind(SyntaxKind.FieldDeclaration))
                Return
            End If

            Debug.Assert(False)
        End Sub

        Public Shared Function IsLambdaBody(node As SyntaxNode) As Boolean
            Dim body As SyntaxNode = Nothing
            Return IsLambdaBodyStatementOrExpression(node, body) AndAlso node Is body
        End Function

        ''' <summary>
        ''' Returns true if the specified <paramref name="node"/> is a statement whose parent is a <see cref="LambdaExpressionSyntax"/>,
        ''' or and expression of a query lambda. Returns the node that represents the body of the lambda in <paramref name="body"/>.
        ''' </summary>
        Public Shared Function IsLambdaBodyStatementOrExpression(node As SyntaxNode, <Out> Optional ByRef body As SyntaxNode = Nothing) As Boolean
            Dim parent = node?.Parent
            If parent Is Nothing Then
                body = Nothing
                Return False
            End If

            Dim body1 As SyntaxNode = Nothing
            Dim body2 As SyntaxNode = Nothing

            Dim result = TryGetLambdaBodies(parent, body1, body2)
            body = If(node Is body2, body2, body1)
            Return result
        End Function

        Public Shared Function TryGetLambdaBodies(node As SyntaxNode, <Out> ByRef body1 As SyntaxNode, <Out> ByRef body2 As SyntaxNode) As Boolean
            body1 = Nothing
            body2 = Nothing

            Select Case node.Kind
                Case SyntaxKind.MultiLineFunctionLambdaExpression,
                     SyntaxKind.SingleLineFunctionLambdaExpression,
                     SyntaxKind.MultiLineSubLambdaExpression,
                     SyntaxKind.SingleLineSubLambdaExpression
                    ' The header of the lambda represents its body.
                    body1 = DirectCast(node, LambdaExpressionSyntax).SubOrFunctionHeader
                    Return True

                Case SyntaxKind.WhereClause
                    body1 = DirectCast(node, WhereClauseSyntax).Condition
                    Return True

                ' source sequence in From and Aggregate (other than the first in the query)
                Case SyntaxKind.CollectionRangeVariable
                    Dim collectionRange = DirectCast(node, CollectionRangeVariableSyntax)
                    If IsFirstInQuery(collectionRange) Then
                        Return False
                    End If

                    body1 = collectionRange.Expression
                    Return True

                ' function call in Group By, Group Join, Aggregate: the argument 
                Case SyntaxKind.FunctionAggregation
                    body1 = DirectCast(node, FunctionAggregationSyntax).Argument
                    Return True

                ' variable in Let, Select, Group By: the RHS
                Case SyntaxKind.ExpressionRangeVariable
                    body1 = DirectCast(node, ExpressionRangeVariableSyntax).Expression
                    Return True

                Case SyntaxKind.TakeWhileClause,
                     SyntaxKind.SkipWhileClause
                    body1 = DirectCast(node, PartitionWhileClauseSyntax).Condition
                    Return True

                Case SyntaxKind.AscendingOrdering,
                     SyntaxKind.DescendingOrdering
                    body1 = DirectCast(node, OrderingSyntax).Expression
                    Return True

                Case SyntaxKind.JoinCondition
                    Dim joinCondition = DirectCast(node, JoinConditionSyntax)
                    body1 = joinCondition.Left
                    body2 = joinCondition.Right
                    Return True
            End Select

            Debug.Assert(Not IsLambda(node))
            Return False
        End Function

        ' TODO(tomat): similar check is needed in breakpoint spans
        Private Shared Function IsFirstInQuery(collectionRangeVariable As CollectionRangeVariableSyntax) As Boolean
            Dim parent = collectionRangeVariable.Parent

            Dim query = DirectCast(parent.Parent, QueryExpressionSyntax)
            If query.Clauses.First() Is parent Then
                Return True
            End If

            Dim variables As SeparatedSyntaxList(Of CollectionRangeVariableSyntax)

            Select Case parent.Kind
                Case SyntaxKind.FromClause
                    variables = DirectCast(parent, FromClauseSyntax).Variables

                Case SyntaxKind.AggregateClause
                    variables = DirectCast(parent, AggregateClauseSyntax).Variables

                Case SyntaxKind.GroupJoinClause, SyntaxKind.SimpleJoinClause
                    variables = DirectCast(parent, JoinClauseSyntax).JoinedVariables

                Case Else
                    Throw ExceptionUtilities.Unreachable
            End Select

            Return variables.IndexOf(collectionRangeVariable) = 0
        End Function

        Public Shared Sub FindLeafNodeAndPartner(leftRoot As SyntaxNode,
                                          leftPosition As Integer,
                                          rightRoot As SyntaxNode,
                                          <Out> ByRef leftNode As SyntaxNode,
                                          <Out> ByRef rightNode As SyntaxNode)
            leftNode = leftRoot
            rightNode = rightRoot
            While True
                Debug.Assert(leftNode.RawKind = rightNode.RawKind)
                Dim childIndex As Integer = 0
                Dim leftChild = leftNode.ChildThatContainsPosition(leftPosition, childIndex)
                If leftChild.IsToken Then
                    Return
                End If

                rightNode = rightNode.ChildNodesAndTokens().ElementAt(childIndex).AsNode()
                leftNode = leftChild.AsNode()
            End While
        End Sub

        Public Shared Function FindPartner(leftRoot As SyntaxNode, rightRoot As SyntaxNode, leftNode As SyntaxNode) As SyntaxNode
            ' Finding a partner of a zero-width node is complicated and not supported atm
            Debug.Assert(leftNode.FullSpan.Length > 0)

            Dim originalLeftNode = leftNode
            Dim leftPosition = leftNode.SpanStart
            leftNode = leftRoot
            Dim rightNode = rightRoot

            While leftNode IsNot originalLeftNode
                Debug.Assert(leftNode.RawKind = rightNode.RawKind)

                Dim childIndex = 0
                Dim leftChild = leftNode.ChildThatContainsPosition(leftPosition, childIndex)

                ' Can only happen when searching for zero-width node.
                Debug.Assert(Not leftChild.IsToken)

                rightNode = rightNode.ChildNodesAndTokens().ElementAt(childIndex).AsNode()
                leftNode = leftChild.AsNode()
            End While

            Return rightNode
        End Function

        Public Shared Function IsNotLambda(node As SyntaxNode) As Boolean
            Return Not IsLambda(node.Kind())
        End Function

        Public Shared Function IsLambda(node As SyntaxNode) As Boolean
            Return IsLambda(node.Kind())
        End Function

        Public Shared Function IsLambda(kind As SyntaxKind) As Boolean
            Select Case kind
                Case SyntaxKind.MultiLineFunctionLambdaExpression,
                     SyntaxKind.SingleLineFunctionLambdaExpression,
                     SyntaxKind.MultiLineSubLambdaExpression,
                     SyntaxKind.SingleLineSubLambdaExpression,
                     SyntaxKind.WhereClause,
                     SyntaxKind.CollectionRangeVariable,
                     SyntaxKind.FunctionAggregation,
                     SyntaxKind.ExpressionRangeVariable,
                     SyntaxKind.TakeWhileClause,
                     SyntaxKind.SkipWhileClause,
                     SyntaxKind.AscendingOrdering,
                     SyntaxKind.DescendingOrdering,
                     SyntaxKind.JoinCondition
                    Return True
            End Select

            Return False
        End Function

        Public Shared Function IsMethod(declaration As SyntaxNode) As Boolean
            Select Case declaration.Kind
                Case SyntaxKind.SubBlock,
                     SyntaxKind.FunctionBlock,
                     SyntaxKind.ConstructorBlock,
                     SyntaxKind.OperatorBlock,
                     SyntaxKind.GetAccessorBlock,
                     SyntaxKind.SetAccessorBlock,
                     SyntaxKind.AddHandlerAccessorBlock,
                     SyntaxKind.RemoveHandlerAccessorBlock,
                     SyntaxKind.RaiseEventAccessorBlock
                    Return True

                Case Else
                    Return False
            End Select
        End Function

        Public Shared Function IsParameterlessConstructor(declaration As SyntaxNode) As Boolean
            If Not declaration.IsKind(SyntaxKind.ConstructorBlock) Then
                Return False
            End If

            Dim ctor = DirectCast(declaration, ConstructorBlockSyntax)
            Return ctor.BlockStatement.ParameterList.Parameters.Count = 0
        End Function

        Public Shared Function HasBackingField(propertyDeclaration As SyntaxNode) As Boolean
            Return propertyDeclaration.IsKind(SyntaxKind.PropertyStatement) AndAlso
                   Not DirectCast(propertyDeclaration, PropertyStatementSyntax).Modifiers.Any(SyntaxKind.MustOverrideKeyword)
        End Function

        Public Shared Function IsAsyncMethodOrLambda(declaration As SyntaxNode) As Boolean
            Return GetModifiers(declaration).Any(SyntaxKind.AsyncKeyword)
        End Function

        Public Shared Function IsIteratorMethodOrLambda(declaration As SyntaxNode) As Boolean
            Return GetModifiers(declaration).Any(SyntaxKind.IteratorKeyword)
        End Function

        Public Shared Function GetAwaitExpressions(body As SyntaxNode) As ImmutableArray(Of SyntaxNode)
            ' skip lambda bodies
            Return ImmutableArray.CreateRange(body.DescendantNodes(Function(n) IsNotLambda(n)).
                Where(Function(n) n.IsKind(SyntaxKind.AwaitExpression)))
        End Function

        Public Shared Function GetYieldStatements(body As SyntaxNode) As ImmutableArray(Of SyntaxNode)
            ' enumerate statements:
            Return ImmutableArray.CreateRange(body.DescendantNodes(Function(n) TypeOf n IsNot ExpressionSyntax).
                Where(Function(n) n.IsKind(SyntaxKind.YieldStatement)))

        End Function

        Public Shared Function GetModifiers(declaration As SyntaxNode) As SyntaxTokenList
            Select Case declaration.Kind
                Case SyntaxKind.SubBlock,
                     SyntaxKind.FunctionBlock
                    Return DirectCast(declaration, MethodBlockBaseSyntax).BlockStatement.Modifiers

                Case SyntaxKind.MultiLineFunctionLambdaExpression,
                     SyntaxKind.SingleLineFunctionLambdaExpression,
                     SyntaxKind.MultiLineSubLambdaExpression,
                     SyntaxKind.SingleLineSubLambdaExpression
                    Return DirectCast(declaration, LambdaExpressionSyntax).SubOrFunctionHeader.Modifiers
            End Select

            Return Nothing
        End Function
    End Class
End Namespace
