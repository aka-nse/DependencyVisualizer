using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DependencyAnalyzer.DataStructure;
using System.Text;
using System.Xml.Linq;

namespace DependencyAnalyzer.SolutionAna
{
    /// <summary>
    /// C#プロジェクト内のクラス間依存関係を高速解析するコアエンジン
    /// パフォーマンスを重視し、並列処理とメモリ効率化を実装した依存関係分析器
    /// </summary>
    public class ProjectAnalyzer
    {
        #region Public Methods

        /// <summary>
        /// 指定されたプロジェクトを包括的に解析し、全クラスの依存関係マップを構築する
        /// </summary>
        /// <param name="project">解析対象のRoslynプロジェクト</param>
        /// <param name="classMap">クラス名をキーとした依存関係オブジェクトのマップ（出力用）</param>
        /// <param name="dependencyNamesMap">クラス名をキーとした依存先クラス名セットのマップ（出力用）</param>
        /// <returns>非同期タスク</returns>
        public async Task AnalyzeAsync(Project project,
            Dictionary<string, ClassDependency> classMap,
            Dictionary<string, HashSet<string>> dependencyNamesMap)
        {
            Console.WriteLine($"Analyzing project: {project.Name}");

            // プロジェクトをコンパイルして、意味解析の基盤となるCompilationオブジェクトを取得
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation == null)
            {
                Console.WriteLine($"  Warning: Could not compile {project.Name}");
                return;
            }

            // コンパイルエラーの検出と警告表示（解析継続のためエラーがあっても処理を続行）
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            if (errors.Any())
            {
                Console.WriteLine($"  Warning: {errors.Length} compilation errors in {project.Name}");
                // 主要なエラーのみ表示してログの肥大化を防ぐ
                foreach (var error in errors.Take(3))
                {
                    Console.WriteLine($"    {error.GetMessage()}");
                }
                if (errors.Length > 3)
                {
                    Console.WriteLine($"    ... and {errors.Length - 3} more errors");
                }
            }

            // 各構文ツリーを並列処理で解析し、マルチコア環境でのパフォーマンスを最大化
            int classCount = 0;
            var tasks = compilation.SyntaxTrees.Select(async tree =>
                await AnalyzeSyntaxTreeOptimized(tree, compilation, classMap, dependencyNamesMap));

            var results = await Task.WhenAll(tasks);
            classCount = results.Sum();

            Console.WriteLine($"  Found {classCount} classes in {project.Name}");
        }

        #endregion

        #region Private Methods - Optimized Analysis

        /// <summary>
        /// 単一の構文ツリーを効率的に解析し、含まれる全ての型宣言を処理する
        /// メモリ使用量を抑制しながら高速処理を実現
        /// </summary>
        /// <param name="tree">解析対象の構文ツリー</param>
        /// <param name="compilation">コンパイル情報</param>
        /// <param name="classMap">クラス依存関係マップ（出力用）</param>
        /// <param name="dependencyNamesMap">依存関係名前マップ（出力用）</param>
        /// <returns>処理されたクラス数</returns>
        private async Task<int> AnalyzeSyntaxTreeOptimized(
            SyntaxTree tree,
            Compilation compilation,
            Dictionary<string, ClassDependency> classMap,
            Dictionary<string, HashSet<string>> dependencyNamesMap)
        {
            // 意味モデルを取得してシンボル情報にアクセス可能にする
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync().ConfigureAwait(false);

            // クラスとインターフェースの宣言を一括取得し、重複処理を避ける
            var typeDeclarations = root.DescendantNodes()
                .Where(node => node is ClassDeclarationSyntax || node is InterfaceDeclarationSyntax)
                .ToList(); // 一度だけ列挙してパフォーマンス向上

            int count = 0;
            foreach (var decl in typeDeclarations)
            {
                try
                {
                    // 各型宣言を個別に処理し、エラー発生時も他の型の処理を継続
                    if (ProcessTypeDeclarationOptimized(decl, semanticModel, classMap, dependencyNamesMap))
                    {
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    // 個別型のエラーは記録するが、全体の解析は継続
                    Console.WriteLine($"型宣言処理中の例外: {ex.Message}");
                }
            }

            return count;
        }

        /// <summary>
        /// 単一の型宣言（クラスまたはインターフェース）を包括的に解析する
        /// 継承関係、メンバー、メソッド内の依存関係を統合的に抽出
        /// partial クラスの重複処理を防ぐため、メンバー情報は初回のみ抽出
        /// </summary>
        /// <param name="decl">型宣言の構文ノード</param>
        /// <param name="semanticModel">意味解析モデル</param>
        /// <param name="classMap">クラス依存関係マップ</param>
        /// <param name="dependencyNamesMap">依存関係名前マップ</param>
        /// <returns>処理が成功した場合true</returns>
        private bool ProcessTypeDeclarationOptimized(
            SyntaxNode decl,
            SemanticModel semanticModel,
            Dictionary<string, ClassDependency> classMap,
            Dictionary<string, HashSet<string>> dependencyNamesMap)
        {
            // 構文ノードから型シンボルを取得
            var symbol = semanticModel.GetDeclaredSymbol(decl);
            if (symbol is not INamedTypeSymbol typeSymbol)
                return false;

            string typeName = typeSymbol.ToString();

            // partial クラスの重複処理を防ぐ
            // ClassDependencyオブジェクトの初期化または既存オブジェクトの取得
            bool isFirstDeclaration = !classMap.ContainsKey(typeName);

            if (isFirstDeclaration)
            {
                classMap[typeName] = new ClassDependency
                {
                    ClassName = typeName,
                    SymbolType = GetSymbolType(typeSymbol)
                };
            }

            var classDep = classMap[typeName];

            // 依存関係の包括的抽出（継承、メンバー、メソッド内使用を統合）
            // partial クラスの各宣言から依存関係を収集（マージ）
            var dependencies = ExtractAllDependencies(typeSymbol, semanticModel, decl);

            // 既存の依存関係とマージ
            if (!dependencyNamesMap.ContainsKey(typeName))
            {
                dependencyNamesMap[typeName] = dependencies;
            }
            else
            {
                // partial クラスの場合、依存関係をマージ
                foreach (var dep in dependencies)
                {
                    dependencyNamesMap[typeName].Add(dep);
                }
            }

            // パブリックメンバー情報は初回のみ抽出（partial クラスの重複を防ぐ）
            // Roslynの型シンボルはpartialクラスの全メンバーを保持しているため、
            // 初回の処理で全メンバーが取得される
            if (isFirstDeclaration)
            {
                ExtractMembers(typeSymbol, classDep);

                // XMLドキュメントコメントからサマリーを抽出
                if (decl is BaseTypeDeclarationSyntax baseDecl)
                {
                    classDep.summary = ExtractSummaryComment(baseDecl);
                }
            }

            return isFirstDeclaration; // 初回のみカウント
        }

        /// <summary>
        /// [依存 #1-#25] 指定された型の全ての依存関係を統合的に抽出する高速化アルゴリズム
        /// 継承、メンバー、メソッド内使用、属性、ジェネリック制約の5つの観点から依存関係を分析
        /// </summary>
        /// <param name="classSymbol">解析対象の型シンボル</param>
        /// <param name="semanticModel">意味解析モデル</param>
        /// <param name="decl">型宣言の構文ノード</param>
        /// <returns>依存先型名のセット</returns>
        private HashSet<string> ExtractAllDependencies(INamedTypeSymbol classSymbol, SemanticModel semanticModel, SyntaxNode decl)
        {
            var dependencies = new HashSet<string>();

            // 1. 継承とインターフェース実装による直接的な依存関係
            ExtractInheritanceDependencies(classSymbol, dependencies);

            // 2. フィールド、プロパティ、メソッドシグネチャによる依存関係
            ExtractMemberDependencies(classSymbol, dependencies);

            // 3. メソッド実装内での型使用による間接的な依存関係
            if (decl is ClassDeclarationSyntax classDecl)
            {
                AnalyzeMethodBodiesOptimized(classDecl, semanticModel, dependencies);
            }

            // 4. 属性（Attribute）の依存関係を抽出
            ExtractAttributeDependencies(decl, semanticModel, dependencies);

            // 5. ジェネリック制約（where句）の依存関係を抽出
            ExtractGenericConstraints(classSymbol, dependencies);

            return dependencies;
        }

        /// <summary>
        /// メソッド本体内の型使用を効率的に解析する最適化エンジン
        /// var宣言、オブジェクト生成、メソッド呼び出し、型キャスト、パターンマッチングなど
        /// 多様な構文パターンから依存関係を抽出
        /// </summary>
        /// <param name="classDecl">クラス宣言構文</param>
        /// <param name="semanticModel">意味解析モデル</param>
        /// <param name="dependencies">依存関係セット（出力用）</param>
        private void AnalyzeMethodBodiesOptimized(
            ClassDeclarationSyntax classDecl,
            SemanticModel semanticModel,
            HashSet<string> dependencies)
        {
            try
            {
                // 全ての子ノードを一度に取得してメモリ効率とパフォーマンスを両立
                var syntaxNodes = classDecl.DescendantNodes().ToList();

                // 型推論を含むvar宣言の解析
                ProcessVarDeclarations(syntaxNodes, semanticModel, dependencies);

                // new演算子によるオブジェクト生成の解析
                ProcessObjectCreations(syntaxNodes, semanticModel, dependencies);

                // メソッド呼び出しによる型依存関係の解析
                ProcessMethodInvocations(syntaxNodes, semanticModel, dependencies);

                // 静的プロパティ/フィールドアクセスの解析
                ProcessStaticMemberAccess(syntaxNodes, semanticModel, dependencies);

                // catch句での例外型の解析
                ProcessCatchClauses(syntaxNodes, semanticModel, dependencies);

                // 型キャスト（cast, as, is）の解析
                ProcessTypeCasts(syntaxNodes, semanticModel, dependencies);

                // typeof演算子の解析
                ProcessTypeOfExpressions(syntaxNodes, semanticModel, dependencies);

                // switch式のパターンマッチングの解析
                ProcessSwitchExpressions(syntaxNodes, semanticModel, dependencies);

                // nameof演算子の解析
                ProcessNameOfExpressions(syntaxNodes, semanticModel, dependencies);

                // default演算子の解析
                ProcessDefaultExpressions(syntaxNodes, semanticModel, dependencies);

                // タプル型の解析
                ProcessTupleTypes(syntaxNodes, semanticModel, dependencies);
            }
            catch (Exception ex)
            {
                // メソッド本体解析エラーは記録するが、他の解析は継続
                Console.WriteLine($"メソッド本体解析中の例外: {ex.Message}");
            }
        }

        /// <summary>
        /// [依存 #8] 静的プロパティ/フィールドへのアクセスを解析し、依存関係を抽出する
        /// 例: SomeClass.StaticProperty, SomeClass.StaticField, MappsXFormatKeys.Id
        /// ラムダ式内やメソッド引数内のメンバーアクセスも包括的に検出
        /// </summary>
        private void ProcessStaticMemberAccess(List<SyntaxNode> syntaxNodes, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            // 全てのメンバーアクセス式を取得
            // メソッド呼び出しの引数内やラムダ式内のメンバーアクセスも含める
            var memberAccesses = syntaxNodes.OfType<MemberAccessExpressionSyntax>();

            foreach (var memberAccess in memberAccesses)
            {
                try
                {
                    // メソッド呼び出しの直接の子（メソッド名部分）は除外
                    // 例: obj.Method() の "obj.Method" 部分は ProcessMethodInvocations で処理済み
                    if (memberAccess.Parent is InvocationExpressionSyntax invocation &&
                        invocation.Expression == memberAccess)
                    {
                        continue;
                    }

                    // アクセスされるメンバーのシンボル情報を取得
                    var symbolInfo = semanticModel.GetSymbolInfo(memberAccess);
                    if (symbolInfo.Symbol == null) continue;

                    // #8: 静的プロパティ・フィールドアクセス
                    // 静的プロパティ、フィールド、const フィールドの場合
                    if (symbolInfo.Symbol is IPropertySymbol property && property.IsStatic)
                    {
                        var containingType = property.ContainingType;
                        if (containingType != null &&
                            !containingType.ToString().StartsWith("System."))
                        {
                            dependencies.Add(containingType.ToString());
                        }
                    }
                    else if (symbolInfo.Symbol is IFieldSymbol field && field.IsStatic)
                    {
                        // const フィールドと通常の static フィールドの両方を処理
                        var containingType = field.ContainingType;
                        if (containingType != null &&
                            !containingType.ToString().StartsWith("System."))
                        {
                            dependencies.Add(containingType.ToString());
                        }
                    }

                    // インスタンスメンバーアクセスの左辺の型も考慮
                    // 例: someObject.Property の someObject の型
                    if (!symbolInfo.Symbol.IsStatic)
                    {
                        var expressionType = semanticModel.GetTypeInfo(memberAccess.Expression);
                        if (expressionType.Type != null)
                        {
                            AddTypeIfRelevant(dependencies, expressionType.Type);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"静的メンバーアクセス解析エラー: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// [依存 #10] catch句で指定された例外型を解析し、依存関係を抽出する
        /// 例: catch (TmsException ex) { }, catch (CustomException) { }
        /// </summary>
        private void ProcessCatchClauses(List<SyntaxNode> syntaxNodes, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            var catchClauses = syntaxNodes.OfType<CatchClauseSyntax>();

            foreach (var catchClause in catchClauses)
            {
                try
                {
                    // #10: 例外処理（catch）
                    // catch句の例外型宣言を取得
                    if (catchClause.Declaration?.Type != null)
                    {
                        var exceptionTypeInfo = semanticModel.GetTypeInfo(catchClause.Declaration.Type);
                        if (exceptionTypeInfo.Type != null)
                        {
                            AddTypeIfRelevant(dependencies, exceptionTypeInfo.Type);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"catch句解析エラー: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// [依存 #12, #13, #14] 型キャストと型チェック演算子を解析し、依存関係を抽出する
        /// 例: (SomeType)value, value as SomeType, value is SomeType
        /// </summary>
        private void ProcessTypeCasts(List<SyntaxNode> syntaxNodes, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            // #12: 型キャスト - キャスト式: (SomeType)value
            var castExpressions = syntaxNodes.OfType<CastExpressionSyntax>();
            foreach (var cast in castExpressions)
            {
                try
                {
                    var typeInfo = semanticModel.GetTypeInfo(cast.Type);
                    if (typeInfo.Type != null)
                    {
                        AddTypeIfRelevant(dependencies, typeInfo.Type);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"キャスト式解析エラー: {ex.Message}");
                }
            }

            // #13: as演算子 - as演算子: value as SomeType
            var binaryExpressions = syntaxNodes
                .OfType<BinaryExpressionSyntax>()
                .Where(be => be.IsKind(SyntaxKind.AsExpression));

            foreach (var asExpr in binaryExpressions)
            {
                try
                {
                    var typeInfo = semanticModel.GetTypeInfo(asExpr.Right);
                    if (typeInfo.Type != null)
                    {
                        AddTypeIfRelevant(dependencies, typeInfo.Type);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"as演算子解析エラー: {ex.Message}");
                }
            }

            // #14: is演算子・パターンマッチング - is演算子とパターンマッチング: value is SomeType
            var isPatternExpressions = syntaxNodes.OfType<IsPatternExpressionSyntax>();
            foreach (var isPattern in isPatternExpressions)
            {
                try
                {
                    // パターン内の型を抽出
                    if (isPattern.Pattern is DeclarationPatternSyntax declPattern)
                    {
                        var typeInfo = semanticModel.GetTypeInfo(declPattern.Type);
                        if (typeInfo.Type != null)
                        {
                            AddTypeIfRelevant(dependencies, typeInfo.Type);
                        }
                    }
                    else if (isPattern.Pattern is ConstantPatternSyntax constantPattern)
                    {
                        var typeInfo = semanticModel.GetTypeInfo(constantPattern.Expression);
                        if (typeInfo.Type != null)
                        {
                            AddTypeIfRelevant(dependencies, typeInfo.Type);
                        }
                    }
                    else if (isPattern.Pattern is RecursivePatternSyntax recursivePattern &&
                             recursivePattern.Type != null)
                    {
                        var typeInfo = semanticModel.GetTypeInfo(recursivePattern.Type);
                        if (typeInfo.Type != null)
                        {
                            AddTypeIfRelevant(dependencies, typeInfo.Type);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"isパターン解析エラー: {ex.Message}");
                }
            }

            // #14: is演算子・パターンマッチング - 従来のis演算子（二項演算子として）: value is SomeType
            var isBinaryExpressions = syntaxNodes
                .OfType<BinaryExpressionSyntax>()
                .Where(be => be.IsKind(SyntaxKind.IsExpression));

            foreach (var isExpr in isBinaryExpressions)
            {
                try
                {
                    var typeInfo = semanticModel.GetTypeInfo(isExpr.Right);
                    if (typeInfo.Type != null)
                    {
                        AddTypeIfRelevant(dependencies, typeInfo.Type);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"is演算子解析エラー: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// [依存 #16] 型、メンバー、パラメータに付与された属性を解析し、依存関係を抽出する
        /// 例: [SomeAttribute], [Required(ErrorMessage = "...")]
        /// </summary>
        private void ExtractAttributeDependencies(SyntaxNode decl, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            try
            {
                // #16: 属性（Attribute）
                // 型宣言自体の属性
                var attributes = decl.DescendantNodes()
                    .OfType<AttributeSyntax>()
                    .ToList();

                foreach (var attribute in attributes)
                {
                    try
                    {
                        var symbolInfo = semanticModel.GetSymbolInfo(attribute);
                        if (symbolInfo.Symbol is IMethodSymbol methodSymbol) // コンストラクタ
                        {
                            var attributeType = methodSymbol.ContainingType;
                            if (attributeType != null)
                            {
                                AddTypeIfRelevant(dependencies, attributeType);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"属性解析エラー: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"属性抽出エラー: {ex.Message}");
            }
        }

        /// <summary>
        /// [依存 #15] typeof演算子を解析し、参照される型への依存関係を抽出する
        /// 例: typeof(SomeClass), Type.GetType("SomeClass")
        /// </summary>
        private void ProcessTypeOfExpressions(List<SyntaxNode> syntaxNodes, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            var typeofExpressions = syntaxNodes.OfType<TypeOfExpressionSyntax>();

            foreach (var typeofExpr in typeofExpressions)
            {
                try
                {
                    // #15: typeof演算子
                    var typeInfo = semanticModel.GetTypeInfo(typeofExpr.Type);
                    if (typeInfo.Type != null)
                    {
                        AddTypeIfRelevant(dependencies, typeInfo.Type);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"typeof解析エラー: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// [依存 #22] switch式のパターンマッチングを解析し、使用される型への依存関係を抽出する
        /// 例: data switch { CustomerDto c => ..., OrderInfo o => ... }
        /// </summary>
        private void ProcessSwitchExpressions(List<SyntaxNode> syntaxNodes, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            var switchExpressions = syntaxNodes.OfType<SwitchExpressionSyntax>();

            foreach (var switchExpr in switchExpressions)
            {
                try
                {
                    // #22: switch式のパターンマッチング
                    foreach (var arm in switchExpr.Arms)
                    {
                        // パターン内の型を抽出
                        ExtractTypesFromPattern(arm.Pattern, semanticModel, dependencies);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"switch式解析エラー: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// パターンから型情報を再帰的に抽出する
        /// </summary>
        private void ExtractTypesFromPattern(PatternSyntax pattern, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            try
            {
                switch (pattern)
                {
                    case DeclarationPatternSyntax declPattern:
                        var typeInfo = semanticModel.GetTypeInfo(declPattern.Type);
                        if (typeInfo.Type != null)
                        {
                            AddTypeIfRelevant(dependencies, typeInfo.Type);
                        }
                        break;

                    case RecursivePatternSyntax recursivePattern:
                        if (recursivePattern.Type != null)
                        {
                            var recTypeInfo = semanticModel.GetTypeInfo(recursivePattern.Type);
                            if (recTypeInfo.Type != null)
                            {
                                AddTypeIfRelevant(dependencies, recTypeInfo.Type);
                            }
                        }
                        // プロパティパターン内の型も処理
                        if (recursivePattern.PositionalPatternClause != null)
                        {
                            foreach (var subpattern in recursivePattern.PositionalPatternClause.Subpatterns)
                            {
                                ExtractTypesFromPattern(subpattern.Pattern, semanticModel, dependencies);
                            }
                        }
                        if (recursivePattern.PropertyPatternClause != null)
                        {
                            foreach (var subpattern in recursivePattern.PropertyPatternClause.Subpatterns)
                            {
                                ExtractTypesFromPattern(subpattern.Pattern, semanticModel, dependencies);
                            }
                        }
                        break;

                    case VarPatternSyntax varPattern:
                        // var パターンは型情報なし
                        break;

                    case ConstantPatternSyntax constantPattern:
                        var constTypeInfo = semanticModel.GetTypeInfo(constantPattern.Expression);
                        if (constTypeInfo.Type != null)
                        {
                            AddTypeIfRelevant(dependencies, constTypeInfo.Type);
                        }
                        break;

                    case BinaryPatternSyntax binaryPattern:
                        // and/or パターン
                        ExtractTypesFromPattern(binaryPattern.Left, semanticModel, dependencies);
                        ExtractTypesFromPattern(binaryPattern.Right, semanticModel, dependencies);
                        break;

                    case UnaryPatternSyntax unaryPattern:
                        // not パターン
                        ExtractTypesFromPattern(unaryPattern.Pattern, semanticModel, dependencies);
                        break;

                    case ParenthesizedPatternSyntax parenthesizedPattern:
                        ExtractTypesFromPattern(parenthesizedPattern.Pattern, semanticModel, dependencies);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"パターン解析エラー: {ex.Message}");
            }
        }

        /// <summary>
        /// [依存 #23] nameof演算子を解析し、参照される型への依存関係を抽出する
        /// 例: nameof(CustomerDto), nameof(CustomerDto.Name)
        /// </summary>
        private void ProcessNameOfExpressions(List<SyntaxNode> syntaxNodes, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            // #23: nameof演算子
            // nameofはInvocationExpressionSyntaxとして表現される
            var invocations = syntaxNodes.OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.Expression is IdentifierNameSyntax id && id.Identifier.Text == "nameof");

            foreach (var invocation in invocations)
            {
                try
                {
                    if (invocation.ArgumentList.Arguments.Count > 0)
                    {
                        var argument = invocation.ArgumentList.Arguments[0].Expression;

                        // nameof(Type) の場合
                        if (argument is IdentifierNameSyntax identifierName)
                        {
                            var symbolInfo = semanticModel.GetSymbolInfo(identifierName);
                            if (symbolInfo.Symbol is INamedTypeSymbol typeSymbol)
                            {
                                AddTypeIfRelevant(dependencies, typeSymbol);
                            }
                        }
                        // nameof(Type.Member) の場合
                        else if (argument is MemberAccessExpressionSyntax memberAccess)
                        {
                            var typeInfo = semanticModel.GetTypeInfo(memberAccess.Expression);
                            if (typeInfo.Type != null)
                            {
                                AddTypeIfRelevant(dependencies, typeInfo.Type);
                            }

                            // メンバー自体のシンボル情報も確認
                            var memberSymbolInfo = semanticModel.GetSymbolInfo(memberAccess);
                            if (memberSymbolInfo.Symbol?.ContainingType != null)
                            {
                                AddTypeIfRelevant(dependencies, memberSymbolInfo.Symbol.ContainingType);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"nameof解析エラー: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// [依存 #24] default演算子を解析し、指定された型への依存関係を抽出する
        /// 例: default(CustomerDto), return default(T)
        /// </summary>
        private void ProcessDefaultExpressions(List<SyntaxNode> syntaxNodes, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            // #24: default演算子
            // C# 7.0以前の default(T) 形式
            var defaultExpressions = syntaxNodes.OfType<DefaultExpressionSyntax>();

            foreach (var defaultExpr in defaultExpressions)
            {
                try
                {
                    var typeInfo = semanticModel.GetTypeInfo(defaultExpr.Type);
                    if (typeInfo.Type != null)
                    {
                        AddTypeIfRelevant(dependencies, typeInfo.Type);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"default式解析エラー: {ex.Message}");
                }
            }

            // C# 7.1以降の default リテラル（型推論）
            var defaultLiterals = syntaxNodes.OfType<LiteralExpressionSyntax>()
                .Where(lit => lit.IsKind(SyntaxKind.DefaultLiteralExpression));

            foreach (var defaultLit in defaultLiterals)
            {
                try
                {
                    // 型推論された型を取得
                    var typeInfo = semanticModel.GetTypeInfo(defaultLit);
                    if (typeInfo.Type != null)
                    {
                        AddTypeIfRelevant(dependencies, typeInfo.Type);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"defaultリテラル解析エラー: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// [依存 #21] タプル型を解析し、要素の型への依存関係を抽出する
        /// 例: (CustomerDto, OrderInfo), (string Name, int Age)
        /// </summary>
        private void ProcessTupleTypes(List<SyntaxNode> syntaxNodes, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            // #21: タプル型
            // タプル型の宣言
            var tupleTypes = syntaxNodes.OfType<TupleTypeSyntax>();

            foreach (var tupleType in tupleTypes)
            {
                try
                {
                    foreach (var element in tupleType.Elements)
                    {
                        var typeInfo = semanticModel.GetTypeInfo(element.Type);
                        if (typeInfo.Type != null)
                        {
                            AddTypeIfRelevant(dependencies, typeInfo.Type);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"タプル型解析エラー: {ex.Message}");
                }
            }

            // タプル式（値タプル）
            var tupleExpressions = syntaxNodes.OfType<TupleExpressionSyntax>();

            foreach (var tupleExpr in tupleExpressions)
            {
                try
                {
                    // タプル全体の型情報を取得
                    var tupleTypeInfo = semanticModel.GetTypeInfo(tupleExpr);
                    if (tupleTypeInfo.Type is INamedTypeSymbol namedType && namedType.IsTupleType)
                    {
                        // タプルの各要素の型を抽出
                        foreach (var element in namedType.TupleElements)
                        {
                            AddTypeIfRelevant(dependencies, element.Type);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"タプル式解析エラー: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// [依存 #25] ジェネリック型パラメータの制約（where句）を解析し、制約型への依存関係を抽出する
        /// 例: where T : BaseEntity, IEntity
        /// </summary>
        private void ExtractGenericConstraints(INamedTypeSymbol typeSymbol, HashSet<string> dependencies)
        {
            try
            {
                // #25: ジェネリック制約（where句）
                // 型パラメータの制約を処理
                foreach (var typeParam in typeSymbol.TypeParameters)
                {
                    // 基底型制約
                    if (typeParam.ConstraintTypes.Length > 0)
                    {
                        foreach (var constraintType in typeParam.ConstraintTypes)
                        {
                            AddTypeIfRelevant(dependencies, constraintType);
                        }
                    }
                }

                // メソッドのジェネリック制約も処理
                foreach (var member in typeSymbol.GetMembers())
                {
                    if (member is IMethodSymbol methodSymbol && methodSymbol.TypeParameters.Length > 0)
                    {
                        foreach (var typeParam in methodSymbol.TypeParameters)
                        {
                            if (typeParam.ConstraintTypes.Length > 0)
                            {
                                foreach (var constraintType in typeParam.ConstraintTypes)
                                {
                                    AddTypeIfRelevant(dependencies, constraintType);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ジェネリック制約解析エラー: {ex.Message}");
            }
        }

        /// <summary>
        /// [依存 #17] var宣言における型推論を解析し、実際の型依存関係を抽出する
        /// 初期化式から実際の型を特定して依存関係に追加
        /// </summary>
        /// <param name="syntaxNodes">解析対象の構文ノード一覧</param>
        /// <param name="semanticModel">意味解析モデル</param>
        /// <param name="dependencies">依存関係セット</param>
        private void ProcessVarDeclarations(List<SyntaxNode> syntaxNodes, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            // #17: var宣言（型推論）
            // var宣言のみを効率的にフィルタリング
            var varDeclarations = syntaxNodes
                .OfType<VariableDeclarationSyntax>()
                .Where(vd => vd.Type.IsVar);

            foreach (var varDecl in varDeclarations)
            {
                foreach (var variable in varDecl.Variables)
                {
                    // 初期化式が存在する場合のみ処理
                    if (variable.Initializer?.Value == null) continue;

                    try
                    {
                        // 初期化式の型情報を取得して実際の型を特定
                        var initializerTypeInfo = semanticModel.GetTypeInfo(variable.Initializer.Value);
                        if (initializerTypeInfo.Type != null)
                        {
                            AddTypeIfRelevant(dependencies, initializerTypeInfo.Type);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"var宣言解析エラー: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// [依存 #5, #11] new演算子によるオブジェクト生成を解析し、型依存関係を抽出する
        /// 生成される型とコンテキスト情報の両方を考慮した包括的分析
        /// throw文での例外生成も含む
        /// </summary>
        /// <param name="syntaxNodes">解析対象の構文ノード一覧</param>
        /// <param name="semanticModel">意味解析モデル</param>
        /// <param name="dependencies">依存関係セット</param>
        private void ProcessObjectCreations(List<SyntaxNode> syntaxNodes, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            var objectCreations = syntaxNodes.OfType<ObjectCreationExpressionSyntax>();

            foreach (var creation in objectCreations)
            {
                try
                {
                    // #5: オブジェクト生成（new）
                    // #11: 例外スロー（throw new Exception）
                    // セマンティックモデルから直接型を取得（より確実）
                    var symbolInfo = semanticModel.GetSymbolInfo(creation);
                    if (symbolInfo.Symbol is IMethodSymbol constructorSymbol)
                    {
                        // コンストラクタの所属する型を取得
                        var createdType = constructorSymbol.ContainingType;
                        if (createdType != null)
                        {
                            AddTypeIfRelevant(dependencies, createdType);
                        }
                    }
                    else
                    {
                        // フォールバック: 従来の方法
                        var createdTypeInfo = semanticModel.GetTypeInfo(creation);
                        if (createdTypeInfo.Type != null)
                        {
                            AddTypeIfRelevant(dependencies, createdTypeInfo.Type);
                        }
                    }

                    // オブジェクト生成のコンテキストを分析（代入先の型など）
                    AnalyzeCreationContext(creation, semanticModel, dependencies);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"オブジェクト生成解析エラー: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// [依存 #6, #7, #9, #18] メソッド呼び出しを解析し、静的メソッド呼び出しとインスタンスメソッド呼び出しを区別して処理
        /// 拡張メソッドの定義クラスも依存関係として検出
        /// 呼び出し先の型情報を抽出して依存関係に追加
        /// </summary>
        /// <param name="syntaxNodes">解析対象の構文ノード一覧</param>
        /// <param name="semanticModel">意味解析モデル</param>
        /// <param name="dependencies">依存関係セット</param>
        private void ProcessMethodInvocations(List<SyntaxNode> syntaxNodes, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            var invocations = syntaxNodes.OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                try
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is not IMethodSymbol methodSymbol) continue;

                    // #7: 拡張メソッド呼び出し
                    if (methodSymbol.IsExtensionMethod)
                    {
                        // 拡張メソッドを定義しているクラスへの依存
                        var containingType = methodSymbol.ContainingType;
                        if (containingType != null &&
                            !containingType.ToString().StartsWith("System."))
                        {
                            dependencies.Add(containingType.ToString());
                        }

                        // 拡張メソッドの第一引数の型（拡張される型）への依存
                        // 例: this JObject jObject の JObject
                        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                        {
                            var extendedTypeInfo = semanticModel.GetTypeInfo(memberAccess.Expression);
                            if (extendedTypeInfo.Type != null)
                            {
                                AddTypeIfRelevant(dependencies, extendedTypeInfo.Type);
                            }
                        }
                    }
                    // #6: 静的メソッド呼び出しの場合（Systemタイプは除外）
                    else if (methodSymbol.IsStatic &&
                        !methodSymbol.ContainingType.ContainingNamespace.ToString().StartsWith("System"))
                    {
                        dependencies.Add(methodSymbol.ContainingType.ToString());
                    }
                    // #18: インスタンスメソッド呼び出しの場合
                    else if (!methodSymbol.IsStatic &&
                             invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                    {
                        var objectTypeInfo = semanticModel.GetTypeInfo(memberAccess.Expression);
                        if (objectTypeInfo.Type != null)
                        {
                            AddTypeIfRelevant(dependencies, objectTypeInfo.Type);
                        }
                    }

                    // #9: ジェネリック型引数 - ジェネリックメソッドの型引数を処理
                    // 例: AssignIfExists<string>(...) の string
                    if (methodSymbol.IsGenericMethod)
                    {
                        foreach (var typeArg in methodSymbol.TypeArguments)
                        {
                            AddTypeIfRelevant(dependencies, typeArg);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"メソッド呼び出し解析エラー: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// オブジェクト生成のコンテキスト（代入先、宣言型など）を分析する軽量アルゴリズム
        /// 変数宣言時の型情報や代入式の左辺型を抽出
        /// </summary>
        /// <param name="creation">オブジェクト生成式</param>
        /// <param name="semanticModel">意味解析モデル</param>
        /// <param name="dependencies">依存関係セット</param>
        private void AnalyzeCreationContext(ObjectCreationExpressionSyntax creation, SemanticModel semanticModel, HashSet<string> dependencies)
        {
            var parent = creation.Parent;

            switch (parent)
            {
                // 変数宣言での初期化の場合
                case EqualsValueClauseSyntax equalsValue
                    when equalsValue.Parent is VariableDeclaratorSyntax declarator &&
                         declarator.Parent is VariableDeclarationSyntax declaration:

                    // varでない明示的型宣言の場合、宣言された型を依存関係に追加
                    if (!declaration.Type.IsVar)
                    {
                        var declaredTypeInfo = semanticModel.GetTypeInfo(declaration.Type);
                        if (declaredTypeInfo.Type != null)
                        {
                            AddTypeIfRelevant(dependencies, declaredTypeInfo.Type);
                        }
                    }
                    break;

                // 代入式の右辺の場合
                case AssignmentExpressionSyntax assignment:
                    var leftTypeInfo = semanticModel.GetTypeInfo(assignment.Left);
                    if (leftTypeInfo.Type != null)
                    {
                        AddTypeIfRelevant(dependencies, leftTypeInfo.Type);
                    }
                    break;
            }
        }

        #endregion

        #region Private Methods - Core Functionality

        /// <summary>
        /// 型シンボルから適切なSymbolTypeを判定する分類アルゴリズム
        /// クラス、インターフェース、構造体、列挙型、デリゲートを区別
        /// </summary>
        /// <param name="symbol">分類対象の型シンボル</param>
        /// <returns>対応するSymbolType</returns>
        private static SymbolType GetSymbolType(INamedTypeSymbol symbol)
        {
            if (symbol.TypeKind == TypeKind.Class)
            {
                // 抽象クラス、インターフェース実装クラス、通常クラスの判定
                if (symbol.IsAbstract && !symbol.IsSealed)
                    return SymbolType.AbstractClass;
                else if (symbol.AllInterfaces.Length > 0)
                    return SymbolType.InterfaceImplementingClass;
                else
                    return SymbolType.Class;
            }

            // その他の型種別の判定
            return symbol.TypeKind switch
            {
                TypeKind.Interface => SymbolType.Interface,
                TypeKind.Struct => SymbolType.Struct,
                TypeKind.Enum => SymbolType.Enum,
                TypeKind.Delegate => SymbolType.Delegate,
                _ => SymbolType.Unknown,
            };
        }

        /// <summary>
        /// 型のパブリックメンバー（フィールド、プロパティ、メソッド）を抽出する
        /// APIの公開インターフェースを特定して依存関係解析に活用
        /// </summary>
        /// <param name="typeSymbol">解析対象の型シンボル</param>
        /// <param name="classDep">メンバー情報を格納するClassDependencyオブジェクト</param>
        private static void ExtractMembers(INamedTypeSymbol typeSymbol, ClassDependency classDep)
        {
            foreach (var member in typeSymbol.GetMembers())
            {
                // パブリックメンバーのみを対象とする
                if (member.DeclaredAccessibility != Accessibility.Public)
                    continue;

                switch (member)
                {
                    case IFieldSymbol field:
                        classDep.Fields.Add(field.Name);
                        break;
                    case IPropertySymbol prop:
                        classDep.Fields.Add(prop.Name);
                        break;
                    case IMethodSymbol method when !method.IsImplicitlyDeclared && method.MethodKind == MethodKind.Ordinary:
                        classDep.Methods.Add(method.Name);
                        break;
                }
            }
        }

        /// <summary>
        /// [依存 #1, #2] 継承関係とインターフェース実装による直接的な依存関係を抽出する
        /// System名前空間の型は除外してユーザー定義型のみを対象とする
        /// </summary>
        /// <param name="classSymbol">解析対象のクラスシンボル</param>
        /// <param name="dependencies">依存関係セット</param>
        private static void ExtractInheritanceDependencies(INamedTypeSymbol classSymbol, HashSet<string> dependencies)
        {
            // #1: 継承 - 基底クラスの処理（Object型とSystem型は除外）
            if (classSymbol.BaseType != null &&
                classSymbol.BaseType.Name != "Object" &&
                !classSymbol.BaseType.ToString().StartsWith("System."))
            {
                dependencies.Add(classSymbol.BaseType.ToString());
            }

            // #2: インターフェース実装 - 実装インターフェースの処理（System型は除外）
            foreach (var interfaceType in classSymbol.Interfaces)
            {
                if (!interfaceType.ToString().StartsWith("System."))
                {
                    dependencies.Add(interfaceType.ToString());
                }
            }
        }

        /// <summary>
        /// [依存 #3, #4] メンバー（フィールド、プロパティ、メソッド）の型シグネチャから依存関係を抽出する
        /// 戻り値型、パラメータ型、フィールド型を包括的に分析
        /// </summary>
        /// <param name="classSymbol">解析対象のクラスシンボル</param>
        /// <param name="dependencies">依存関係セット</param>
        private void ExtractMemberDependencies(INamedTypeSymbol classSymbol, HashSet<string> dependencies)
        {
            foreach (var member in classSymbol.GetMembers())
            {
                switch (member)
                {
                    case IFieldSymbol field:
                        // #3: フィールド・プロパティの型
                        AddTypeIfRelevant(dependencies, field.Type);
                        break;
                    case IPropertySymbol prop:
                        // #3: フィールド・プロパティの型
                        AddTypeIfRelevant(dependencies, prop.Type);
                        break;
                    case IMethodSymbol method when !method.IsImplicitlyDeclared:
                        // #4: メソッドシグネチャ - メソッドの戻り値型を追加
                        AddTypeIfRelevant(dependencies, method.ReturnType);
                        // #4: メソッドシグネチャ - パラメータ型を追加
                        foreach (var param in method.Parameters)
                        {
                            AddTypeIfRelevant(dependencies, param.Type);
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// 型が依存関係として記録すべきかを判定し、適切な場合に追加する核心ロジック
        /// ビルトイン型、System型、ジェネリック型を適切に処理
        /// </summary>
        /// <param name="dependencies">依存関係セット</param>
        /// <param name="type">評価対象の型</param>
        private void AddTypeIfRelevant(HashSet<string> dependencies, ITypeSymbol type)
        {
            if (type == null) return;

            var typeName = type.ToString();

            // ビルトイン型とSpecialType（primitiveタイプ）は除外
            if (IsBuiltInType(typeName) || type.SpecialType != SpecialType.None)
                return;

            // 一般的なSystem型は除外（一部の汎用コレクションは含める）
            if (IsSystemTypeToExclude(typeName))
                return;

            // 型の種類に応じた処理
            ProcessTypeByKind(dependencies, type, typeName);
        }

        /// <summary>
        /// System名前空間の型のうち、依存関係から除外すべき型を判定する
        /// Dictionary、Listなどの汎用コレクションは依存関係として重要なため含める
        /// </summary>
        /// <param name="typeName">判定対象の型名</param>
        /// <returns>除外すべき場合true</returns>
        private static bool IsSystemTypeToExclude(string typeName)
        {
            // 依存関係として含めたい例外リスト
            var allowedPrefixes = new[]
            {
                "System.Collections.Generic.List",
                "System.Collections.Generic.Dictionary",
                "System.Collections.Generic.IEnumerable",
                "System.Collections.Generic.ICollection",
                "System.Collections.Generic.IList",
                "System.Collections.Generic.HashSet",
                "System.Threading.Tasks.Task"
            };

            // System名前空間で、例外リストに含まれないものは除外
            return typeName.StartsWith("System.") &&
                   !allowedPrefixes.Any(prefix => typeName.StartsWith(prefix));
        }

        /// <summary>
        /// 型の種類（配列、ジェネリック、通常の型）に応じて適切な依存関係抽出を実行
        /// 配列の要素型、ジェネリック型引数を再帰的に処理
        /// </summary>
        /// <param name="dependencies">依存関係セット</param>
        /// <param name="type">処理対象の型</param>
        /// <param name="typeName">型名</param>
        private void ProcessTypeByKind(HashSet<string> dependencies, ITypeSymbol type, string typeName)
        {
            switch (type)
            {
                case IArrayTypeSymbol arrayType:
                    // 配列の要素型を再帰的に処理
                    AddTypeIfRelevant(dependencies, arrayType.ElementType);
                    break;

                case INamedTypeSymbol namedType when namedType.IsGenericType:
                    // ジェネリック型引数を再帰的に処理
                    foreach (var typeArg in namedType.TypeArguments)
                    {
                        AddTypeIfRelevant(dependencies, typeArg);
                    }

                    // ジェネリック型自体も依存関係として追加
                    if ((namedType.TypeKind == TypeKind.Class || namedType.TypeKind == TypeKind.Interface) &&
                        !IsSystemTypeToExclude(typeName))
                    {
                        dependencies.Add(typeName);
                    }
                    break;

                default:
                    // 通常のクラス・インターフェース型
                    if (type.TypeKind == TypeKind.Class || type.TypeKind == TypeKind.Interface)
                    {
                        dependencies.Add(typeName);
                    }
                    break;
            }
        }

        /// <summary>
        /// C#のビルトイン型（プリミティブ型）を判定する
        /// これらの型は依存関係として記録する必要がない
        /// </summary>
        /// <param name="typeName">判定対象の型名</param>
        /// <returns>ビルトイン型の場合true</returns>
        private static bool IsBuiltInType(string typeName)
        {
            var builtInTypes = new HashSet<string>
            {
                "string", "int", "bool", "double", "float", "decimal",
                "long", "short", "byte", "char", "object", "void"
            };
            return builtInTypes.Contains(typeName);
        }

        /// <summary>
        /// 型宣言のXMLドキュメントコメントからsummaryタグの内容を抽出する
        /// APIドキュメント生成や依存関係理由の理解に活用
        /// </summary>
        /// <param name="decl">型宣言構文</param>
        /// <returns>抽出されたサマリー文字列</returns>
        private static string ExtractSummaryComment(BaseTypeDeclarationSyntax decl)
        {
            // 型宣言の先頭トリビア（コメント）を取得
            var trivia = decl.GetLeadingTrivia();
            var docCommentTrivia = trivia
                .Select(t => t.GetStructure())
                .OfType<DocumentationCommentTriviaSyntax>()
                .FirstOrDefault();

            if (docCommentTrivia == null)
                return string.Empty;

            // XMLドキュメントコメントからsummaryタグを抽出
            var xmlText = new StringBuilder();
            foreach (var node in docCommentTrivia.Content)
            {
                if (node is XmlElementSyntax element &&
                    element.StartTag.Name.LocalName.Text == "summary")
                {
                    xmlText.Append(string.Concat(element.Content.Select(c => c.ToString())));
                }
            }

            // コメント記号を除去してクリーンなテキストを返す
            var summary = xmlText.ToString().Trim();
            return summary.Replace("///", "").Trim();
        }

        #endregion
    }
}
