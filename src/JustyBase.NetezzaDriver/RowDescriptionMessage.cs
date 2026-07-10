using JustyBase.NetezzaDriver.StringPool;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JustyBase.NetezzaDriver;

internal sealed class RowDescriptionMessage
{
    private readonly Dictionary<string, int> _nameIndex;
    private readonly FieldDescription?[] _fields;
    public RowDescriptionMessage(int numFields)
    {
        _fields = new FieldDescription[numFields];
        _nameIndex = new Dictionary<string, int>();
    }
    public FieldDescription this[int ordinal]
    {
        get => _fields[ordinal]!;
        set
        { 
            _fields[ordinal] = value;
            _nameIndex[value.Name] = ordinal;
        }
    }
    public int FieldCount => _fields.Length;
    public int FieldIndex(string name) => _nameIndex[name];
}
internal sealed class FieldDescription
{
    internal string Name { get; set; } = null!;
    internal uint TypeOID { get; set; }
    public short TypeSize { get; set; }
    public int TypeModifier { get; set; }
    public byte DataFormat { get; set; }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public Type Type => TypeOID switch
    {
        16 => typeof(bool), // Boolean
        17 => typeof(string), // SELECT * FROM SYSTEM.ADMIN._T_PROC ORDER BY ROWID LIMIT 500
        18 => typeof(string), // SELECT * FROM SYSTEM.ADMIN._T_ATTRIBUTE ORDER BY ROWID LIMIT 500
        19 => typeof(string), // name type (system..)
        20 => typeof(long), // bigint
        21 => typeof(short), //????
        22 => typeof(string), //SELECT * FROM SYSTEM.ADMIN._T_INDEX ORDER BY ROWID LIMIT 500
        23 => typeof(int), // integer
        24 => typeof(string), //  SELECT AGGTRANSFN FROM SYSTEM.ADMIN._T_AGGREGATE ORDER BY ROWID LIMIT 500
        25 => typeof(string),//????
        26 => typeof(int), // oid..
        30 => typeof(string), // SELECT * FROM SYSTEM.ADMIN._T_AGGREGATE ORDER BY ROWID LIMIT 500 // 0,1,..[13]
        700 => typeof(float), // DbRead
        701 => typeof(double), //DbFloat
        702 => typeof(DateTime), // SELECT CREATEDATE FROM _V_TABLE ORDER BY CREATEDATE DESC
        705 => typeof(string), //unknown?
        1042 => typeof(string),//char 
        1043 => typeof(string),//????
        1082 => typeof(DateTime),// Date
        1083 => typeof(TimeSpan), // Time
        1114 => typeof(DateTime),//????
        1184 => typeof(DateTime),//????
        //1186 => typeof(TimeSpan),
        1186 => typeof(string), // interval caanot be represented as TimeSpan -> lack of months..
        1700 => typeof(decimal), // numeric
        2500 => typeof(Int16),// Byteint not SByte bocouse of ODBC compatibility.. (mailny for green test and compliance)
        2522 => typeof(string), // nchar
        2530 => typeof(string), // Nvarchar
        //_ => typeof(object)//????
        _ => typeof(string)//????
    };
    public Sylvan StringPool { get; set; } = null!;
}
