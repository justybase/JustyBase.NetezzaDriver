using System.Text;

namespace JustyBase.NetezzaDriver;

internal static class NzParameterHelper
{
    internal static string SubstituteParameters(string sql, NzParameterCollection parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return sql;

        bool hasPositional = false;
        bool hasNamed = false;
        foreach (NzParameter p in parameters)
        {
            if (p.IsPositional)
                hasPositional = true;
            else
                hasNamed = true;
        }

        if (hasNamed)
            return SubstituteNamed(sql, parameters);

        if (hasPositional)
            return SubstitutePositional(sql, parameters);

        return sql;
    }

    private static string SubstituteNamed(string sql, NzParameterCollection parameters)
    {
        var sb = new StringBuilder(sql.Length + 256);
        int i = 0;

        while (i < sql.Length)
        {
            if (sql[i] == '\'')
            {
                sb.Append('\'');
                i++;
                while (i < sql.Length)
                {
                    sb.Append(sql[i]);
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            i++;
                            sb.Append(sql[i]);
                        }
                        else
                        {
                            i++;
                            break;
                        }
                    }
                    i++;
                }
            }
            else if (sql[i] == '"')
            {
                sb.Append('"');
                i++;
                while (i < sql.Length && sql[i] != '"')
                {
                    sb.Append(sql[i]);
                    i++;
                }
                if (i < sql.Length)
                {
                    sb.Append('"');
                    i++;
                }
            }
            else if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                sb.Append("--");
                i += 2;
                while (i < sql.Length && sql[i] != '\n')
                {
                    sb.Append(sql[i]);
                    i++;
                }
            }
            else if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                sb.Append("/*");
                i += 2;
                while (i + 1 < sql.Length)
                {
                    if (sql[i] == '*' && sql[i + 1] == '/')
                    {
                        sb.Append("*/");
                        i += 2;
                        break;
                    }
                    sb.Append(sql[i]);
                    i++;
                }
            }
            else if ((sql[i] == ':' || sql[i] == '@') && i + 1 < sql.Length && IsIdentifierStart(sql[i + 1]))
            {
                int start = i;
                char prefix = sql[i];
                i++;
                int nameStart = i;
                while (i < sql.Length && IsIdentifierPart(sql[i]))
                    i++;

                string paramName = sql[nameStart..i];
                string lookup = prefix + paramName;

                NzParameter? param = null;
                foreach (NzParameter p in parameters)
                {
                    if (!p.IsPositional && string.Equals(p.ResolvedName, paramName, StringComparison.OrdinalIgnoreCase))
                    {
                        param = p;
                        break;
                    }
                    if (!p.IsPositional && string.Equals(p.ParameterName, lookup, StringComparison.OrdinalIgnoreCase))
                    {
                        param = p;
                        break;
                    }
                }

                if (param is not null)
                {
                    sb.Append(param.ToSqlLiteral());
                }
                else
                {
                    sb.Append(prefix);
                    sb.Append(paramName);
                }
            }
            else if (sql[i] == '?')
            {
                sb.Append('?');
                i++;
            }
            else
            {
                sb.Append(sql[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    private static string SubstitutePositional(string sql, NzParameterCollection parameters)
    {
        var paramValues = new List<string>(parameters.Count);
        foreach (NzParameter p in parameters)
            paramValues.Add(p.ToSqlLiteral());

        var sb = new StringBuilder(sql.Length + 256);
        int i = 0;
        int paramIndex = 0;

        while (i < sql.Length)
        {
            if (sql[i] == '\'')
            {
                sb.Append('\'');
                i++;
                while (i < sql.Length)
                {
                    sb.Append(sql[i]);
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            i++;
                            sb.Append(sql[i]);
                        }
                        else
                        {
                            i++;
                            break;
                        }
                    }
                    i++;
                }
            }
            else if (sql[i] == '"')
            {
                sb.Append('"');
                i++;
                while (i < sql.Length && sql[i] != '"')
                {
                    sb.Append(sql[i]);
                    i++;
                }
                if (i < sql.Length)
                {
                    sb.Append('"');
                    i++;
                }
            }
            else if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                sb.Append("--");
                i += 2;
                while (i < sql.Length && sql[i] != '\n')
                {
                    sb.Append(sql[i]);
                    i++;
                }
            }
            else if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                sb.Append("/*");
                i += 2;
                while (i + 1 < sql.Length)
                {
                    if (sql[i] == '*' && sql[i + 1] == '/')
                    {
                        sb.Append("*/");
                        i += 2;
                        break;
                    }
                    sb.Append(sql[i]);
                    i++;
                }
            }
            else if (sql[i] == '?' && paramIndex < paramValues.Count)
            {
                sb.Append(paramValues[paramIndex]);
                paramIndex++;
                i++;
            }
            else
            {
                sb.Append(sql[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    private static bool IsIdentifierStart(char c)
    {
        return char.IsLetter(c) || c == '_';
    }

    private static bool IsIdentifierPart(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_' || c == '.';
    }
}
