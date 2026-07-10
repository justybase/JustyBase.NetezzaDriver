namespace JustyBase.NetezzaDriver;

/// <summary>
/// Represents a prepared SQL statement.
/// </summary>
internal sealed class PreparedStatement
{
    /// <summary>
    /// Gets or sets the description of the row returned by the prepared statement.
    /// </summary>
    public RowDescriptionMessage? Description { get; set; }

    /// <summary>
    /// Gets the number of fields in the row description.
    /// </summary>
    public int FieldCount => Description?.FieldCount ?? -1;

    /// <summary>
    /// Gets or sets the SQL query of the prepared statement.
    /// </summary>
    public string? Sql { get; set; }
}

