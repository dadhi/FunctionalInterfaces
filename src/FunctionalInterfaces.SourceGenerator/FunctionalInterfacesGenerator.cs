﻿using System.CodeDom.Compiler;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FunctionalInterfaces.SourceGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class FunctionalInterfacesGenerator : IIncrementalGenerator
{
	private static readonly Dictionary<int, IMethodSymbol> emptyDictionary = new();

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		IncrementalValuesProvider<(MethodDeclarationSyntax Method, SemanticModel SemanticModel)> methodValuesProvider = context.SyntaxProvider.CreateSyntaxProvider(
			static (node, _) => node is MethodDeclarationSyntax,
			static (context, _) => ((MethodDeclarationSyntax)context.Node, context.SemanticModel));

		context.RegisterImplementationSourceOutput(methodValuesProvider, static (sourceProductionContext, value) =>
		{
			(MethodDeclarationSyntax method, SemanticModel semanticModel) = value;

			List<(InvocationExpressionSyntax Invocation, IMethodSymbol Candidate, Dictionary<int, IMethodSymbol> FunctionalInterfaces)>? candidates = null;
			foreach (SyntaxNode node in method.DescendantNodes())
			{
				if (node is not InvocationExpressionSyntax invocation)
				{
					continue;
				}

				(IMethodSymbol? candidate, Dictionary<int, IMethodSymbol> functionalInterfaces) = FunctionalInterfacesGenerator.FindFunctionalInterfaceTarget(semanticModel, invocation);
				if (candidate is null)
				{
					continue;
				}

				candidates ??= new List<(InvocationExpressionSyntax Invocation, IMethodSymbol Candidate, Dictionary<int, IMethodSymbol> FunctionalInterfaces)>();
				candidates.Add((invocation, candidate, functionalInterfaces));
			}

			if (candidates is null)
			{
				return;
			}

			StringBuilder hintNameBuilder = new();

			using StringWriter stream = new();
			using (IndentedTextWriter writer = new(stream, "\t"))
			{
				writer.WriteLine("// <auto-generated/>");
				writer.WriteLine("#nullable enable");
				writer.WriteLine("#pragma warning disable");
				writer.WriteLine();

				List<ClassDeclarationSyntax> hierarchy = FunctionalInterfacesGenerator.GetHierarchy(method);

				BaseNamespaceDeclarationSyntax? namespaceDeclaration = null;
				for (SyntaxNode? node = hierarchy[hierarchy.Count - 1]; node != null; node = node.Parent)
				{
					if (node is CompilationUnitSyntax compilationUnit)
					{
						writer.WriteLine(compilationUnit.Usings);
						writer.WriteLine();

						break;
					}
					else if (node is BaseNamespaceDeclarationSyntax syntax)
					{
						namespaceDeclaration = syntax;
					}
				}

				if (namespaceDeclaration is not null)
				{
					hintNameBuilder.Append(namespaceDeclaration.Name.ToString().Replace('.', '_'));
					hintNameBuilder.Append('_');

					writer.WriteLine($"namespace {namespaceDeclaration.Name}");
					writer.WriteLine("{");
					writer.Indent++;
				}

				for (int j = hierarchy.Count - 1; j >= 0; j--)
				{
					ClassDeclarationSyntax classDeclaration = hierarchy[j];

					hintNameBuilder.Append(classDeclaration.Identifier);
					hintNameBuilder.Append('_');

					if (classDeclaration.Arity > 0)
					{
						hintNameBuilder.Append(classDeclaration.Arity);
						hintNameBuilder.Append('_');
					}

					writer.WriteLine($"partial class {classDeclaration.Identifier.ToString() + classDeclaration.TypeParameterList}");
					writer.WriteLine("{");
					writer.Indent++;
				}

				LambdaExpressionSyntax lambda = (LambdaExpressionSyntax)candidates[0].Invocation.ArgumentList.Arguments[candidates[0].FunctionalInterfaces.Keys.First()].Expression;

				string typeName = $"{method.Identifier}_{method.Span.Start:X}_{lambda.Span.Start:X}".Replace('<', '_').Replace('>', '_');

				SyntaxNode methodBody = method.Body!.TrackNodes(candidates.Select(candidate => candidate.Invocation));
				foreach ((InvocationExpressionSyntax Invocation, IMethodSymbol Candidate, Dictionary<int, IMethodSymbol> FunctionalInterfaces) valueTuple in candidates)
				{
					SyntaxNode invocation = FunctionalInterfacesGenerator.GenerateFunctionalInterfaces(writer, semanticModel, method, valueTuple.Invocation, valueTuple.Candidate, valueTuple.FunctionalInterfaces, typeName);

					DataFlowAnalysis? dataFlowAnalysis = semanticModel.AnalyzeDataFlow(lambda);

					if (dataFlowAnalysis is not null)
					{
						methodBody = methodBody.ReplaceNodes(methodBody.DescendantNodes(), (original, modified) =>
						{
							if (modified is LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 } localDeclaration)
							{
								bool captured = dataFlowAnalysis.Captured.Any(i => i.Name == localDeclaration.Declaration.Variables[0].Identifier.Text);
								if (captured)
								{
									return SyntaxFactory.ExpressionStatement(
										SyntaxFactory.AssignmentExpression(
											SyntaxKind.SimpleAssignmentExpression,
											SyntaxFactory.MemberAccessExpression(
												SyntaxKind.SimpleMemberAccessExpression,
												SyntaxFactory.IdentifierName("__functionalInterface"),
												SyntaxFactory.IdentifierName(localDeclaration.Declaration.Variables[0].Identifier)),
											localDeclaration.Declaration.Variables[0].Initializer!.Value));
								}
							}
							else if (modified is IdentifierNameSyntax { Parent: not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "__functionalInterface" } } } identifier)
							{
								bool captured = dataFlowAnalysis.Captured.Any(i => i.Name == identifier.Identifier.Text);
								if (captured)
								{
									return SyntaxFactory.MemberAccessExpression(
										SyntaxKind.SimpleMemberAccessExpression,
										SyntaxFactory.IdentifierName("__functionalInterface"), identifier);
								}
							}

							return modified;
						});
					}

					if (methodBody.GetCurrentNode(valueTuple.Invocation) is { } currentNode)
					{
						if (dataFlowAnalysis is not null && currentNode.Parent is ExpressionStatementSyntax expression)
						{
							List<ExpressionStatementSyntax> initVariables = new();
							foreach (ISymbol symbol in dataFlowAnalysis.DataFlowsIn)
							{
								if (symbol is IParameterSymbol { IsThis: true })
								{
									initVariables.Add(SyntaxFactory.ExpressionStatement(
										SyntaxFactory.AssignmentExpression(
											SyntaxKind.SimpleAssignmentExpression,
											SyntaxFactory.MemberAccessExpression(
												SyntaxKind.SimpleMemberAccessExpression,
												SyntaxFactory.IdentifierName("__functionalInterface"),
												SyntaxFactory.IdentifierName("_this")),
											SyntaxFactory.ThisExpression())));

									continue;
								}

								foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
								{
									SyntaxNode declaration = reference.GetSyntax();
									if (declaration is VariableDeclaratorSyntax)
									{
										continue;
									}

									initVariables.Add(SyntaxFactory.ExpressionStatement(
										SyntaxFactory.AssignmentExpression(
											SyntaxKind.SimpleAssignmentExpression,
											SyntaxFactory.MemberAccessExpression(
												SyntaxKind.SimpleMemberAccessExpression,
												SyntaxFactory.IdentifierName("__functionalInterface"),
												SyntaxFactory.IdentifierName(symbol.Name)),
											SyntaxFactory.IdentifierName(symbol.Name))));
								}
							}

							methodBody = methodBody.InsertNodesBefore(expression, initVariables);
						}

						methodBody = methodBody.ReplaceNode(methodBody.GetCurrentNode(valueTuple.Invocation)!, invocation);
					}

					writer.WriteLine();
				}

				writer.WriteLine($"private {(method.Modifiers.Any(SyntaxKind.StaticKeyword) ? "static " : string.Empty)}{method.ReturnType} {method.Identifier}_FunctionalInterface({method.ParameterList.Parameters})");
				writer.WriteLine("{");
				writer.Indent++;

				writer.WriteLine($"{typeName} __functionalInterface = default;");

				writer.WriteLineNoTabs(methodBody.NormalizeWhitespace().ToFullString());

				writer.Indent--;
				writer.WriteLine("}");

				for (int j = namespaceDeclaration is not null ? -1 : 0; j < hierarchy.Count; j++)
				{
					writer.Indent--;
					writer.WriteLine("}");
				}
			}

			hintNameBuilder.Append(method.Identifier);
			hintNameBuilder.Append(".g.cs");

			string hintName = hintNameBuilder.ToString();
			string source = stream.ToString();

			sourceProductionContext.AddSource(hintName, source);
		});
	}

	private static (IMethodSymbol? Target, Dictionary<int, IMethodSymbol> FunctionalInterfaces) FindFunctionalInterfaceTarget(SemanticModel semanticModel, InvocationExpressionSyntax invocation)
	{
		HashSet<int>? candidateParams = null;
		for (int i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
		{
			ArgumentSyntax argument = invocation.ArgumentList.Arguments[i];
			if (argument.Expression is ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax)
			{
				candidateParams ??= new HashSet<int>();
				candidateParams.Add(i);
			}
		}

		if (candidateParams is null)
		{
			return (null, FunctionalInterfacesGenerator.emptyDictionary);
		}

		SymbolInfo invocationTargetInfo = semanticModel.GetSymbolInfo(invocation);
		if (invocationTargetInfo.Symbol is not IMethodSymbol invocationTarget)
		{
			return (null, FunctionalInterfacesGenerator.emptyDictionary);
		}

		Dictionary<int, IMethodSymbol>? functionalInterfaces = null;
		foreach (IMethodSymbol candidate in invocationTarget.ContainingType.GetMembers(invocationTarget.Name)
					 .Where(symbol => !SymbolEqualityComparer.Default.Equals(symbol, invocationTarget) && symbol is IMethodSymbol { Arity: > 0 } methodSymbol && methodSymbol.Parameters.Length == invocationTarget.Parameters.Length)
					 .Cast<IMethodSymbol>())
		{
			for (int i = 0; i < candidate.Parameters.Length; i++)
			{
				IParameterSymbol candidateParameter = candidate.Parameters[i];
				IParameterSymbol targetParameter = invocationTarget.Parameters[i];
				if (!candidateParams.Contains(i))
				{
					if (candidateParameter.Type is ITypeParameterSymbol)
					{
						continue;
					}

					if (!SymbolEqualityComparer.Default.Equals(candidateParameter.Type, targetParameter.Type))
					{
						goto CONTINUE;
					}
				}
				else if (candidateParameter.Type is not ITypeParameterSymbol { ConstraintTypes.Length: 1 })
				{
					goto CONTINUE;
				}
			}

			foreach (int i in candidateParams)
			{
				ITypeParameterSymbol candidateParameter = (ITypeParameterSymbol)candidate.Parameters[i].Type;
				ITypeSymbol functionalInterfaceType = candidateParameter.ConstraintTypes[0];
				if (functionalInterfaceType.TypeKind != TypeKind.Interface)
				{
					goto CONTINUE;
				}

				ImmutableArray<ISymbol> targetCandidates = functionalInterfaceType.GetMembers();
				if (targetCandidates.Length != 1 || targetCandidates[0] is not IMethodSymbol targetSymbol)
				{
					goto CONTINUE;
				}

				functionalInterfaces ??= new Dictionary<int, IMethodSymbol>();
				functionalInterfaces.Add(i, targetSymbol);
			}

			return (candidate, functionalInterfaces!);

		CONTINUE:
			;
		}

		return (null, FunctionalInterfacesGenerator.emptyDictionary);
	}

	private static List<ClassDeclarationSyntax> GetHierarchy(SyntaxNode? node)
	{
		List<ClassDeclarationSyntax> hierarchy = new();

		while (node != null)
		{
			if (node is ClassDeclarationSyntax classDeclaration)
			{
				hierarchy.Add(classDeclaration);
			}

			node = node.Parent;
		}

		return hierarchy;
	}

	private static SyntaxNode GenerateFunctionalInterfaces(IndentedTextWriter writer, SemanticModel semanticModel, MethodDeclarationSyntax method, InvocationExpressionSyntax invocation, IMethodSymbol candidate, Dictionary<int, IMethodSymbol> functionalInterfaces, string firstTypeName)
	{
		string typeName = string.Empty;
		foreach (KeyValuePair<int, IMethodSymbol> kvp in functionalInterfaces)
		{
			(int i, IMethodSymbol candidateTarget) = (kvp.Key, kvp.Value);

			LambdaExpressionSyntax lambda = (LambdaExpressionSyntax)invocation.ArgumentList.Arguments[i].Expression;

			ITypeSymbol returnType = candidateTarget.ReturnType;
			INamedTypeSymbol containingSymbol = candidateTarget.ContainingType;
			if (containingSymbol.IsGenericType)
			{
				ITypeSymbol[] resolvedGenerics = containingSymbol.TypeArguments.ToArray();

				SymbolInfo lambdaSymbolInfo = semanticModel.GetSymbolInfo(lambda);
				if (lambdaSymbolInfo.Symbol is IMethodSymbol lambdaSymbol)
				{
					if (returnType is ITypeParameterSymbol returnTypeParameter)
					{
						int parameterIndex = containingSymbol.TypeArguments.IndexOf(returnTypeParameter);
						if (parameterIndex != -1)
						{
							resolvedGenerics[parameterIndex] = returnType = lambdaSymbol.ReturnType;
						}
					}

					for (int j = 0; j < candidateTarget.Parameters.Length; j++)
					{
						IParameterSymbol parameter = candidateTarget.Parameters[j];
						if (parameter.Type is ITypeParameterSymbol parameterType)
						{
							int parameterIndex = containingSymbol.TypeArguments.IndexOf(parameterType);
							if (parameterIndex != -1)
							{
								resolvedGenerics[parameterIndex] = lambdaSymbol.Parameters[j].Type;
							}
						}
					}
				}

				containingSymbol = containingSymbol.OriginalDefinition.Construct(resolvedGenerics);
				candidateTarget = (IMethodSymbol)containingSymbol.GetMembers(candidateTarget.Name).Single();
			}

			typeName = $"{method.Identifier}_{method.Span.Start:X}_{lambda.Span.Start:X}".Replace('<', '_').Replace('>', '_');

			DataFlowAnalysis? dataFlowAnalysis = semanticModel.AnalyzeDataFlow(lambda);

			//TODO: Auto layout?
			writer.WriteLine($"private struct {typeName} : {containingSymbol}");
			writer.WriteLine("{");
			writer.Indent++;

			if (dataFlowAnalysis is not null)
			{
				foreach (ISymbol symbol in dataFlowAnalysis.Captured)
				{
					if (symbol is ILocalSymbol local)
					{
						writer.WriteLine($"public {local.Type} {symbol.Name};");
					}
					else if (symbol is IParameterSymbol param)
					{
						if (param.Name == "this")
						{
							writer.WriteLine($"public {param.Type} _{param.Name};");
						}
						else
						{
							writer.WriteLine($"public {param.Type} {param.Name};");
						}
					}
				}
			}

			writer.WriteLine();
			writer.WriteLine($"public {(lambda.AsyncKeyword != default ? "async " : string.Empty)}{returnType} {candidateTarget.Name}({string.Join(", ", candidateTarget.Parameters)})");

			SyntaxNode lambdaBody = lambda.Body.ReplaceNodes(lambda.Body.DescendantNodes(), (original, modified) =>
			{
				if (modified is ThisExpressionSyntax)
				{
					return SyntaxFactory.IdentifierName("_this");
				}
				else if (modified is InvocationExpressionSyntax inv)
				{
					return TransformCall((InvocationExpressionSyntax)original, inv);
				}

				return modified;
			});

			writer.WriteLineNoTabs(lambdaBody.NormalizeWhitespace().ToFullString());

			writer.Indent--;
			writer.WriteLine("}");
		}

		return TransformCall(invocation, invocation, true);

		SyntaxNode TransformCall(InvocationExpressionSyntax original, InvocationExpressionSyntax invoke, bool call = false)
		{
			SymbolInfo invokeTargetInfo = semanticModel.GetSymbolInfo(original);
			if (invokeTargetInfo.Symbol is not IMethodSymbol invokeTarget)
			{
				return invoke;
			}

			(IMethodSymbol? candidateSymbol, Dictionary<int, IMethodSymbol> functionalInterfaces) = FunctionalInterfacesGenerator.FindFunctionalInterfaceTarget(semanticModel, original);
			if (candidateSymbol is null)
			{
				return invoke;
			}

			string[] resolvedGenerics = new string[candidateSymbol.TypeParameters.Length];
			foreach (KeyValuePair<int, IMethodSymbol> kvp in functionalInterfaces)
			{
				(int i, IMethodSymbol candidateTarget) = (kvp.Key, kvp.Value);

				ExpressionSyntax lambda = original.ArgumentList.Arguments[i].Expression;

				SymbolInfo lambdaSymbolInfo = semanticModel.GetSymbolInfo(lambda);
				if (lambdaSymbolInfo.Symbol is IMethodSymbol lambdaSymbol)
				{
					if (candidateTarget.ReturnType is ITypeParameterSymbol returnTypeParameter)
					{
						int parameterIndex = candidateSymbol.TypeParameters.IndexOf(returnTypeParameter);
						if (parameterIndex != -1)
						{
							resolvedGenerics[parameterIndex] = lambdaSymbol.ReturnType.ToString();
						}
					}

					for (int j = 0; j < candidateTarget.Parameters.Length; j++)
					{
						IParameterSymbol parameter = candidateTarget.Parameters[j];
						if (parameter.Type is ITypeParameterSymbol parameterType)
						{
							int parameterIndex = candidateSymbol.TypeParameters.IndexOf(parameterType);
							if (parameterIndex != -1)
							{
								resolvedGenerics[parameterIndex] = lambdaSymbol.Parameters[j].Type.ToString();
							}
						}
					}
				}

				string otherName = $"{method.Identifier}_{method.Span.Start:X}_{lambda.Span.Start:X}".Replace('<', '_').Replace('>', '_');

				if (candidate.Parameters[i].Type is ITypeParameterSymbol parameterTypeParameter)
				{
					resolvedGenerics[candidate.TypeParameters.IndexOf(parameterTypeParameter)] = otherName;
				}

				invoke = invoke.ReplaceNode(invoke.ArgumentList.Arguments[i], invoke.ArgumentList.Arguments[i].WithExpression(
					SyntaxFactory.InvocationExpression(
							SyntaxFactory.MemberAccessExpression(
								SyntaxKind.SimpleMemberAccessExpression,
								SyntaxFactory.MemberAccessExpression(
									SyntaxKind.SimpleMemberAccessExpression,
									SyntaxFactory.MemberAccessExpression(
										SyntaxKind.SimpleMemberAccessExpression,
										SyntaxFactory.MemberAccessExpression(
											SyntaxKind.SimpleMemberAccessExpression,
											SyntaxFactory.IdentifierName("System"),
											SyntaxFactory.IdentifierName("Runtime")),
										SyntaxFactory.IdentifierName("CompilerServices")),
									SyntaxFactory.IdentifierName("Unsafe")),
								SyntaxFactory.GenericName(
										SyntaxFactory.Identifier("As"))
									.WithTypeArgumentList(
										SyntaxFactory.TypeArgumentList(
											SyntaxFactory.SeparatedList<TypeSyntax>(new SyntaxNodeOrToken[]
												{
																SyntaxFactory.IdentifierName(call ? firstTypeName : typeName),
																SyntaxFactory.Token(SyntaxKind.CommaToken),
																SyntaxFactory.IdentifierName(call ? typeName : otherName)
												})))))
						.WithArgumentList(
							SyntaxFactory.ArgumentList(
								SyntaxFactory.SingletonSeparatedList(
									SyntaxFactory.Argument(
											call
												? SyntaxFactory.IdentifierName("__functionalInterface")
												: SyntaxFactory.ThisExpression())
										.WithRefOrOutKeyword(
											SyntaxFactory.Token(SyntaxKind.RefKeyword)))))));
			}

			GenericNameSyntax newInvokeTarget = SyntaxFactory.GenericName(invokeTarget.Name)
				.WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
					SyntaxFactory.SeparatedList<TypeSyntax>(
						resolvedGenerics.Select(SyntaxFactory.IdentifierName))));

			if (invoke.Expression is MemberAccessExpressionSyntax memberAccess)
			{
				return invoke.WithExpression(memberAccess.WithName(newInvokeTarget));
			}
			else
			{
				invoke.WithExpression(newInvokeTarget);
			}

			return invoke;
		}
	}
}
