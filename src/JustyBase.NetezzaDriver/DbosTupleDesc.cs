/// <summary>
/// Represents the description of a tuple in the Netezza database.
/// </summary>
internal sealed class DbosTupleDesc
{
    /// <summary>
    /// Gets or sets the version of the tuple.
    /// </summary>
    public int? Version { get; set; }

    /// <summary>
    /// Gets or sets the number of nulls allowed.
    /// </summary>
    public int? NullsAllowed { get; set; }

    /// <summary>
    /// Gets or sets the size word.
    /// </summary>
    public int? SizeWord { get; set; }

    /// <summary>
    /// Gets or sets the size of the size word.
    /// </summary>
    public int? SizeWordSize { get; set; }

    /// <summary>
    /// Gets or sets the number of fixed fields.
    /// </summary>
    public int? NumFixedFields { get; set; }

    /// <summary>
    /// Gets or sets the number of varying fields.
    /// </summary>
    public int? NumVaryingFields { get; set; }

    /// <summary>
    /// Gets or sets the size of the fixed fields.
    /// </summary>
    public int FixedFieldsSize { get; set; }

    /// <summary>
    /// Gets or sets the maximum record size.
    /// </summary>
    public int? MaxRecordSize { get; set; }

    /// <summary>
    /// Gets or sets the total number of fields.
    /// </summary>
    public int NumFields { get; set; }

    /// <summary>
    /// Gets or sets the list of field types.
    /// </summary>
    public List<int> FieldType { get; set; }

    /// <summary>
    /// Gets or sets the list of field sizes.
    /// </summary>
    public List<int> FieldSize { get; set; }

    /// <summary>
    /// Gets or sets the list of true field sizes.
    /// </summary>
    public List<int> FieldTrueSize { get; set; }

    /// <summary>
    /// Gets or sets the list of field offsets.
    /// </summary>
    public List<int> FieldOffset { get; set; }

    /// <summary>
    /// Gets or sets the list of physical field indices.
    /// </summary>
    public List<int> FieldPhysField { get; set; }

    /// <summary>
    /// Gets or sets the list of logical field indices.
    /// </summary>
    public List<int> FieldLogField { get; set; }

    /// <summary>
    /// Gets or sets a list indicating whether each field allows nulls.
    /// </summary>
    public List<bool> FieldNullAllowed { get; set; }

    /// <summary>
    /// Gets or sets the list of fixed field sizes.
    /// </summary>
    public List<int> FieldFixedSize { get; set; }

    /// <summary>
    /// Gets or sets the list of spring field indices.
    /// </summary>
    public List<int> FieldSpringField { get; set; }

    /// <summary>
    /// Gets or sets the date style.
    /// </summary>
    public int? DateStyle { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether European date formats are used.
    /// </summary>
    public int? EuroDates { get; set; }

    /// <summary>
    /// Gets or sets the database character set.
    /// </summary>
    //public string DBCharset { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether 24-hour time format is enabled.
    /// </summary>
    public bool? EnableTime24 { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbosTupleDesc"/> class.
    /// </summary>
    public DbosTupleDesc()
    {
        FieldType = new List<int>();
        FieldSize = new List<int>();
        FieldTrueSize = new List<int>();
        FieldOffset = new List<int>();
        FieldPhysField = new List<int>();
        FieldLogField = new List<int>();
        FieldNullAllowed = new List<bool>();
        FieldFixedSize = new List<int>();
        FieldSpringField = new List<int>();
    }
}
