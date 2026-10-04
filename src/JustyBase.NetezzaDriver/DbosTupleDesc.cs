/// <summary>
/// Represents the description of a tuple in the Netezza database.
/// Parsed metadata and the per-field arrays share one representation: the
/// descriptor payload is copied straight into the final arrays once
/// <see cref="NumFields"/> has been validated, avoiding a duplicate List
/// representation and the copy performed by the former <c>Freeze()</c> step.
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
    /// Gets or sets the date style.
    /// </summary>
    public int? DateStyle { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether European date formats are used.
    /// </summary>
    public int? EuroDates { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether 24-hour time format is enabled.
    /// </summary>
    public bool? EnableTime24 { get; set; }

    // Flat per-field arrays filled directly while parsing the descriptor.
    internal int[] FieldTypeArr { get; private set; } = [];
    internal int[] FieldSizeArr { get; private set; } = [];
    internal int[] FieldTrueSizeArr { get; private set; } = [];
    internal int[] FieldOffsetArr { get; private set; } = [];
    internal int[] FieldPhysFieldArr { get; private set; } = [];
    internal int[] FieldLogFieldArr { get; private set; } = [];
    internal bool[] FieldNullAllowedArr { get; private set; } = [];
    internal int[] FieldFixedSizeArr { get; private set; } = [];
    internal int[] FieldSpringFieldArr { get; private set; } = [];

    /// <summary>
    /// Allocates the per-field arrays exactly once, after the field count has
    /// been validated against the descriptor payload size.
    /// </summary>
    internal void Initialize(int fieldCount)
    {
        if (fieldCount <= 0)
        {
            return;
        }

        FieldTypeArr = new int[fieldCount];
        FieldSizeArr = new int[fieldCount];
        FieldTrueSizeArr = new int[fieldCount];
        FieldOffsetArr = new int[fieldCount];
        FieldPhysFieldArr = new int[fieldCount];
        FieldLogFieldArr = new int[fieldCount];
        FieldNullAllowedArr = new bool[fieldCount];
        FieldFixedSizeArr = new int[fieldCount];
        FieldSpringFieldArr = new int[fieldCount];
    }
}
