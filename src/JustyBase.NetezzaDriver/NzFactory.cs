using System.Data.Common;

namespace JustyBase.NetezzaDriver;

/// <summary>
/// ADO.NET provider factory for <see cref="NzConnection"/>. Register the
/// singleton with <see cref="DbProviderFactories"/> when an application
/// resolves providers by invariant name.
/// </summary>
public sealed class NzFactory : DbProviderFactory
{
    public static readonly NzFactory Instance = new();

    private NzFactory()
    {
    }

    public override DbConnection? CreateConnection() => new NzConnection();

    public override DbCommand? CreateCommand() => new NzCommand();

    public override DbParameter? CreateParameter() => new NzParameter();

    public override DbConnectionStringBuilder? CreateConnectionStringBuilder()
        => new NzConnectionStringBuilder();

    public override bool CanCreateDataSourceEnumerator => false;
}
