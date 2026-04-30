namespace SqlRepoAnalyzer.Core.Queries;

public enum SourceKind
{
    SqlFile = 1,
    EmbeddedRawSql = 2,
    TypeOrmRawQuery = 3,
    TypeOrmQueryBuilderSite = 4,
    TypeOrmQueryDynamic = 5,

    /// <summary>Verbatim / interpolated verbatim string in a C# source file (e.g. EF Core raw SQL).</summary>
    CSharpEmbeddedSql = 6,
}

