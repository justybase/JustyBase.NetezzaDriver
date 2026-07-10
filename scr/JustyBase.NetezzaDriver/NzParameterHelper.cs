using System.Text;

namespace JustyBase.NetezzaDriver;

internal static class NzParameterHelper
{
    internal static string SubstituteParameters(string sql, NzParameterCollection parameters)
    {
        if (parameters is null)
            return sql;

        if (parameters.Count == 0)
        {
            var firstPlaceholder = FindFirstPlaceholder(sql);
            if (firstPlaceholder is not null)
                throw new InvalidOperationException($"Missing value for SQL parameter '{firstPlaceholder}'.");

            return sql;
        }

        bool hasPositional = false;
        bool hasNamed = false;
        foreach (NzParameter p in parameters)
        {
            if (p.IsPositional)
                hasPositional = true;
            else
                hasNamed = true;
        }

        if (hasNamed && hasPositional)
            throw new InvalidOperationException("Named and positional parameters cannot be mixed in the same command.");

        if (hasNamed)
            return SubstituteNamed(sql, parameters);

        if (hasPositional)
            return SubstitutePositional(sql, parameters);

        return sql;
    }

    private static string SubstituteNamed(string sql, NzParameterCollection parameters)
    {
        var sb = new StringBuilder(sql.Length + 256);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            else if (sql[i] == '$' && i + 1 < sql.Length && (sql[i + 1] == '$' || char.IsLetter(sql[i + 1])))
            {
                sb.Append(sql[i]);
                i++;
                int tagStart = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;
                string tag = sql[tagStart..i];
                if (i < sql.Length && sql[i] == '$')
                {
                    sb.Append(tag);
                    sb.Append('$');
                    i++;
                    string endTag = "$" + tag + "$";
                    while (i + endTag.Length <= sql.Length)
                    {
                        sb.Append(sql[i]);
                        if (sql.AsSpan(i, endTag.Length).SequenceEqual(endTag))
                        {
                            for (int k = 1; k < endTag.Length; k++)
                            {
                                i++;
                                sb.Append(sql[i]);
                            }
                            i++;
                            break;
                        }
                        i++;
                    }
                }
                else
                {
                    sb.Append(tag);
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
            else if (sql[i] == ':' && i + 1 < sql.Length && sql[i + 1] == ':')
            {
                sb.Append("::");
                i += 2;
            }
            else if ((sql[i] == ':' || sql[i] == '@') && i + 1 < sql.Length && IsIdentifierStart(sql[i + 1]))
            {
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
                    usedNames.Add(param.ResolvedName ?? paramName);
                }
                else
                {
                    throw new InvalidOperationException($"Missing value for SQL parameter '{lookup}'.");
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

        foreach (NzParameter p in parameters)
        {
            var name = p.ResolvedName;
            if (!p.IsPositional && !string.IsNullOrEmpty(name) && !usedNames.Contains(name))
                throw new InvalidOperationException($"SQL parameter '{p.ParameterName}' was provided but not used.");
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
            else if (sql[i] == '$' && i + 1 < sql.Length && (sql[i + 1] == '$' || char.IsLetter(sql[i + 1])))
            {
                sb.Append(sql[i]);
                i++;
                int tagStart = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;
                string tag = sql[tagStart..i];
                if (i < sql.Length && sql[i] == '$')
                {
                    sb.Append(tag);
                    sb.Append('$');
                    i++;
                    string endTag = "$" + tag + "$";
                    while (i + endTag.Length <= sql.Length)
                    {
                        sb.Append(sql[i]);
                        if (sql.AsSpan(i, endTag.Length).SequenceEqual(endTag))
                        {
                            for (int k = 1; k < endTag.Length; k++)
                            {
                                i++;
                                sb.Append(sql[i]);
                            }
                            i++;
                            break;
                        }
                        i++;
                    }
                }
                else
                {
                    sb.Append(tag);
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
            else if (sql[i] == '?' && paramIndex >= paramValues.Count)
            {
                throw new InvalidOperationException("Not enough positional parameter values were provided.");
            }
            else
            {
                sb.Append(sql[i]);
                i++;
            }
        }

        if (paramIndex < paramValues.Count)
            throw new InvalidOperationException("More positional parameter values were provided than placeholders in SQL.");

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

    private static string? FindFirstPlaceholder(string sql)
    {
        int i = 0;
        while (i < sql.Length)
        {
            if (sql[i] == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'' && (i + 1 >= sql.Length || sql[i + 1] != '\''))
                    {
                        i++;
                        break;
                    }
                    if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                        i++;
                    i++;
                }
            }
            else if (sql[i] == '"')
            {
                i++;
                while (i < sql.Length && sql[i] != '"')
                    i++;
                if (i < sql.Length)
                    i++;
            }
            else if (sql[i] == '$' && i + 1 < sql.Length && (sql[i + 1] == '$' || char.IsLetter(sql[i + 1])))
            {
                i++;
                int tagStart = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;
                string tag = sql[tagStart..i];
                if (i < sql.Length && sql[i] == '$')
                {
                    i++;
                    string endTag = "$" + tag + "$";
                    while (i + endTag.Length <= sql.Length)
                    {
                        if (sql.AsSpan(i, endTag.Length).SequenceEqual(endTag))
                        {
                            i += endTag.Length;
                            break;
                        }
                        i++;
                    }
                }
            }
            else if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\n')
                    i++;
            }
            else if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                    i++;
                if (i + 1 < sql.Length)
                    i += 2;
            }
            else if (sql[i] == ':' && i + 1 < sql.Length && sql[i + 1] == ':')
            {
                i += 2;
            }
            else if ((sql[i] == ':' || sql[i] == '@') && i + 1 < sql.Length && IsIdentifierStart(sql[i + 1]))
            {
                char prefix = sql[i];
                i++;
                int nameStart = i;
                while (i < sql.Length && IsIdentifierPart(sql[i]))
                    i++;
                return prefix + sql[nameStart..i];
            }
            else if (sql[i] == '?')
            {
                return "?";
            }
            else
            {
                i++;
            }
        }

        return null;
    }
}
