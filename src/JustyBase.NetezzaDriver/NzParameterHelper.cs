using System.Buffers;

namespace JustyBase.NetezzaDriver;

internal static class NzParameterHelper
{
    internal readonly struct Placeholder
    {
        public readonly bool IsNamed;
        public readonly int Start;
        public readonly int Length;
        public readonly int NameStart;
        public readonly int NameLength;

        public Placeholder(bool isNamed, int start, int length, int nameStart, int nameLength)
        {
            IsNamed = isNamed;
            Start = start;
            Length = length;
            NameStart = nameStart;
            NameLength = nameLength;
        }
    }

    internal sealed class SqlTemplatePlan
    {
        public readonly Placeholder[] Placeholders;
        public readonly bool HasNamed;
        public readonly bool HasPositional;

        public SqlTemplatePlan(Placeholder[] placeholders, bool hasNamed, bool hasPositional)
        {
            Placeholders = placeholders;
            HasNamed = hasNamed;
            HasPositional = hasPositional;
        }
    }

    internal static string SubstituteParameters(string sql, NzParameterCollection parameters)
    {
        if (parameters is null)
            return sql;

        if (parameters.Count == 0)
        {
            var plan0 = ParseTemplate(sql);
            if (plan0.Placeholders.Length > 0)
            {
                var first = plan0.Placeholders[0];
                string name = first.IsNamed ? sql.Substring(first.Start, first.Length) : "?";
                throw new InvalidOperationException($"Missing value for SQL parameter '{name}'.");
            }
            return sql;
        }

        var plan = ParseTemplate(sql);
        return RenderWithPlan(sql, plan, parameters);
    }

    internal static SqlTemplatePlan ParseTemplate(string sql)
    {
        var placeholders = new List<Placeholder>(8);
        bool hasNamed = false;
        bool hasPositional = false;
        int i = 0;
        int n = sql.Length;

        while (i < n)
        {
            char c = sql[i];
            if (c == '\'')
            {
                i++;
                while (i < n)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < n && sql[i + 1] == '\'')
                            i += 2;
                        else
                        {
                            i++;
                            break;
                        }
                    }
                    else
                        i++;
                }
            }
            else if (c == '"')
            {
                i++;
                while (i < n && sql[i] != '"')
                    i++;
                if (i < n)
                    i++;
            }
            else if (c == '$' && i + 1 < n && (sql[i + 1] == '$' || char.IsLetter(sql[i + 1])))
            {
                int tagStart = i + 1;
                int tagEnd = tagStart;
                while (tagEnd < n && (char.IsLetterOrDigit(sql[tagEnd]) || sql[tagEnd] == '_'))
                    tagEnd++;
                if (tagEnd < n && sql[tagEnd] == '$')
                {
                    int tagLen = tagEnd - tagStart;
                    i = tagEnd + 1;
                    // Scan for $<tag>$ without allocating endTag string.
                    while (i < n)
                    {
                        if (sql[i] != '$')
                        {
                            i++;
                            continue;
                        }
                        // Need tagLen+2 chars starting at i: '$' + tag + '$'.
                        if (i + tagLen + 2 > n)
                        {
                            i++;
                            continue;
                        }
                        if (tagLen == 0)
                        {
                            if (sql[i + 1] == '$')
                            {
                                i += 2;
                                break;
                            }
                            i++;
                        }
                        else
                        {
                            if (sql[i + 1 + tagLen] == '$'
                                && sql.AsSpan(i + 1, tagLen).SequenceEqual(sql.AsSpan(tagStart, tagLen)))
                            {
                                i += tagLen + 2;
                                break;
                            }
                            i++;
                        }
                    }
                }
                else
                {
                    i = tagEnd;
                }
            }
            else if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                i += 2;
                while (i < n && sql[i] != '\n')
                    i++;
            }
            else if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n)
                {
                    if (sql[i] == '*' && sql[i + 1] == '/')
                    {
                        i += 2;
                        break;
                    }
                    i++;
                }
            }
            else if (c == ':' && i + 1 < n && sql[i + 1] == ':')
            {
                i += 2;
            }
            else if ((c == ':' || c == '@') && i + 1 < n && IsIdentifierStart(sql[i + 1]))
            {
                int start = i;
                i++;
                int nameStart = i;
                while (i < n && IsIdentifierPart(sql[i]))
                    i++;
                int nameLen = i - nameStart;
                placeholders.Add(new Placeholder(true, start, i - start, nameStart, nameLen));
                hasNamed = true;
            }
            else if (c == '?')
            {
                placeholders.Add(new Placeholder(false, i, 1, -1, 0));
                hasPositional = true;
                i++;
            }
            else
            {
                i++;
            }
        }

        return new SqlTemplatePlan(placeholders.ToArray(), hasNamed, hasPositional);
    }

    internal static string RenderWithPlan(string sql, SqlTemplatePlan plan, NzParameterCollection parameters)
    {
        bool hasPositional = false;
        bool hasNamed = false;
        int count = parameters.Count;
        for (int pi = 0; pi < count; pi++)
        {
            if (parameters[pi].IsPositional)
                hasPositional = true;
            else
                hasNamed = true;
            if (hasNamed && hasPositional)
                break;
        }

        if (hasNamed && hasPositional)
            throw new InvalidOperationException("Named and positional parameters cannot be mixed in the same command.");

        if (hasNamed)
            return RenderNamed(sql, plan, parameters);

        if (hasPositional)
            return RenderPositional(sql, plan, parameters);

        return sql;
    }

    private static string RenderNamed(string sql, SqlTemplatePlan plan, NzParameterCollection parameters)
    {
        int count = parameters.Count;
        Span<bool> used = count <= 128 ? stackalloc bool[count] : new bool[count];

        // For larger collections a linear scan per placeholder is O(n*m).
        // Build an open-addressing name table once; duplicate names fall back
        // to the original linear scan so their "used" bookkeeping is unchanged.
        const int HashThreshold = 8;
        int[]? rentedBuckets = null;
        Span<int> buckets = default;
        int bucketMask = 0;
        bool useHash = false;
        if (count > HashThreshold)
        {
            int tableSize = 16;
            while (tableSize < count * 2)
                tableSize <<= 1;
            rentedBuckets = ArrayPool<int>.Shared.Rent(tableSize);
            buckets = rentedBuckets.AsSpan(0, tableSize);
            buckets.Fill(-1);
            bucketMask = tableSize - 1;
            bool duplicates = false;
            for (int idx = 0; idx < count && !duplicates; idx++)
            {
                var p = parameters[idx];
                if (p.IsPositional)
                    continue;
                ReadOnlySpan<char> span = p.GetResolvedNameSpan();
                int slot = NameHash(span) & bucketMask;
                while (buckets[slot] != -1)
                {
                    if (parameters[buckets[slot]].GetResolvedNameSpan().Equals(span, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicates = true;
                        break;
                    }
                    slot = (slot + 1) & bucketMask;
                }
                if (!duplicates)
                    buckets[slot] = idx;
            }
            useHash = !duplicates;
        }

        // Rent from the pool instead of a StringBuilder: avoids both the
        // builder object and its private growable char[] per render.
        ValueStringBuilder sb = new ValueStringBuilder(ArrayPool<char>.Shared.Rent(sql.Length + 256));

        try
        {
            int pos = 0;
            var placeholders = plan.Placeholders;

            for (int k = 0; k < placeholders.Length; k++)
            {
                var ph = placeholders[k];
                if (!ph.IsNamed)
                {
                    continue;
                }

                sb.Append(sql, pos, ph.Start - pos);
                ReadOnlySpan<char> nameSpan = sql.AsSpan(ph.NameStart, ph.NameLength);

                NzParameter? match = null;
                int matchIdx = -1;
                if (useHash)
                {
                    int slot = NameHash(nameSpan) & bucketMask;
                    while (buckets[slot] != -1)
                    {
                        int idx = buckets[slot];
                        var p = parameters[idx];
                        if (!p.IsPositional && p.GetResolvedNameSpan().Equals(nameSpan, StringComparison.OrdinalIgnoreCase))
                        {
                            match = p;
                            matchIdx = idx;
                            break;
                        }
                        slot = (slot + 1) & bucketMask;
                    }
                }
                else
                {
                    for (int idx = 0; idx < count; idx++)
                    {
                        var p = parameters[idx];
                        if (p.IsPositional)
                            continue;
                        if (p.GetResolvedNameSpan().Equals(nameSpan, StringComparison.OrdinalIgnoreCase))
                        {
                            match = p;
                            matchIdx = idx;
                            break;
                        }
                    }
                }

                if (match is not null)
                {
                    match.AppendSqlLiteral(ref sb);
                    used[matchIdx] = true;
                    if (!useHash)
                    {
                        // Duplicate parameter entries can resolve to the same name
                        // (e.g. "id" and ":id"). They all map to this placeholder, so
                        // mark every later duplicate as used as well; otherwise they
                        // would be reported as provided but not used. No equal name can
                        // precede matchIdx because the scan stops at the first match.
                        for (int idx = matchIdx + 1; idx < count; idx++)
                        {
                            var p = parameters[idx];
                            if (!p.IsPositional && p.GetResolvedNameSpan().Equals(nameSpan, StringComparison.OrdinalIgnoreCase))
                                used[idx] = true;
                        }
                    }
                }
                else
                {
                    string lookup = sql.Substring(ph.Start, ph.Length);
                    throw new InvalidOperationException($"Missing value for SQL parameter '{lookup}'.");
                }

                pos = ph.Start + ph.Length;
            }

            // Copy tail including any '?' left verbatim plus literals.
            // Named placeholders were skipped above via pos jumps; positional '?'
            // placeholders are part of literal spans (pos only jumps over named).
            // To interleave correctly, we appended literals before each named ph;
            // '?' chars remain in the copied spans. Append remainder:
            sb.Append(sql, pos, sql.Length - pos);

            for (int idx = 0; idx < count; idx++)
            {
                var p = parameters[idx];
                if (!p.IsPositional && !used[idx])
                {
                    var resolved = p.ResolvedName;
                    if (!string.IsNullOrEmpty(resolved))
                        throw new InvalidOperationException($"SQL parameter '{p.ParameterName}' was provided but not used.");
                }
            }

            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
            if (rentedBuckets is not null)
                ArrayPool<int>.Shared.Return(rentedBuckets);
        }
    }

    private static int NameHash(ReadOnlySpan<char> name)
    {
        uint hash = 2166136261u;
        foreach (char c in name)
        {
            hash ^= char.ToUpperInvariant(c);
            hash *= 16777619u;
        }
        return (int)(hash & 0x7FFFFFFF);
    }

    private static string RenderPositional(string sql, SqlTemplatePlan plan, NzParameterCollection parameters)
    {
        int count = parameters.Count;
        // Rent from the pool instead of a StringBuilder: avoids both the
        // builder object and its private growable char[] per render.
        ValueStringBuilder sb = new ValueStringBuilder(ArrayPool<char>.Shared.Rent(sql.Length + 256));

        try
        {
            int pos = 0;
            int paramIndex = 0;
            var placeholders = plan.Placeholders;

            for (int k = 0; k < placeholders.Length; k++)
            {
                var ph = placeholders[k];
                if (ph.IsNamed)
                {
                    continue;
                }

                sb.Append(sql, pos, ph.Start - pos);
                if (paramIndex < count)
                {
                    parameters[paramIndex].AppendSqlLiteral(ref sb);
                    paramIndex++;
                }
                else
                {
                    throw new InvalidOperationException("Not enough positional parameter values were provided.");
                }
                pos = ph.Start + 1;
            }

            sb.Append(sql, pos, sql.Length - pos);

            if (paramIndex < count)
                throw new InvalidOperationException("More positional parameter values were provided than placeholders in SQL.");

            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
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
