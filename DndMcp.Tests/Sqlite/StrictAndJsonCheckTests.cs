using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// Invariant: STRICT tables reject values that cannot be stored losslessly in the declared type, and
/// <c>CHECK (json_valid(col))</c> rejects text that is not RFC 8259 JSON. Both are the database's last line
/// of defence when a repository bug writes the wrong thing (PLAN.md D2, research/04 §4 DDL: every table is
/// STRICT, every JSON column is CHECKed).
///
/// <para>
/// The tests also pin where each guard is weaker than its name suggests, because the repository has to
/// cover those gaps itself. STRICT still converts losslessly (<c>'123'</c> becomes 123 and 5 becomes
/// <c>'5'</c>). json_valid accepts any JSON value, not just objects.
/// </para>
/// </summary>
public sealed class StrictAndJsonCheckTests : IDisposable
{
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintCheck = 275;
    private const int SqliteConstraintDatatype = 3091;

    private readonly SqliteConnection _db;

    public StrictAndJsonCheckTests()
    {
        _db = SqliteScratch.OpenInMemory();
        _db.Execute("""
            CREATE TABLE typed (n INTEGER, r REAL, t TEXT) STRICT;
            CREATE TABLE loose (n INTEGER);
            CREATE TABLE doc (data TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(data))) STRICT;
            CREATE TABLE object_doc (data TEXT NOT NULL CHECK (json_valid(data) AND json_type(data) = 'object')) STRICT;
            """);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// PLAN.md D2 "STRICT tables". A value that cannot convert losslessly is rejected with
    /// SQLITE_CONSTRAINT_DATATYPE, where an ordinary table would store it silently.
    /// </summary>
    [Theory]
    [InlineData("n", "'abc'")]
    [InlineData("n", "1.5")]
    [InlineData("n", "x'00'")]
    [InlineData("r", "'fast'")]
    [InlineData("t", "x'00'")]
    public void Strict_ValueNotLosslesslyConvertible_IsRejected(string column, string literal)
    {
        var exception = Assert.Throws<SqliteException>(() => _db.Execute($"INSERT INTO typed({column}) VALUES ({literal})"));

        Assert.Equal(SqliteConstraint, exception.SqliteErrorCode);
        Assert.Equal(SqliteConstraintDatatype, exception.SqliteExtendedErrorCode);
    }

    /// <summary>
    /// Where STRICT is NOT strict: values that convert losslessly are accepted and stored as the declared
    /// type. A numeric-looking string becomes an INTEGER, and a number bound to a TEXT column becomes text. So
    /// STRICT does not catch a C# <c>long</c> written into a TEXT id column. Parameter types are the
    /// repository's job.
    /// </summary>
    [Theory]
    [InlineData("n", "'123'", "integer")]
    [InlineData("n", "2.0", "integer")]
    [InlineData("r", "3", "real")]
    [InlineData("t", "5", "text")]
    public void Strict_LosslesslyConvertibleValue_IsCoercedToTheDeclaredType(string column, string literal, string storedType)
    {
        _db.Execute($"INSERT INTO typed({column}) VALUES ({literal})");

        Assert.Equal(storedType, _db.ExecuteScalar<string>($"SELECT typeof({column}) FROM typed"));
    }

    /// <summary>
    /// STRICT only allows INTEGER, REAL, TEXT, BLOB and ANY, and checks at CREATE time. A migration that
    /// copies a <c>VARCHAR(10)</c> habit from another database fails loudly instead of being read as TEXT
    /// affinity.
    /// </summary>
    [Fact]
    public void Strict_UnknownDeclaredType_IsRejectedAtCreate()
    {
        var exception = Assert.Throws<SqliteException>(() => _db.Execute("CREATE TABLE bad (name VARCHAR(10)) STRICT"));

        Assert.Contains("unknown datatype", exception.Message);
    }

    /// <summary>
    /// The counterfactual: without STRICT the same wrong-typed insert succeeds and stores text in an INTEGER
    /// column. This is what the STRICT keyword on every table in the DDL prevents.
    /// </summary>
    [Fact]
    public void NonStrict_WrongTypedInsert_IsSilentlyStored()
    {
        _db.Execute("INSERT INTO loose(n) VALUES ('abc')");

        Assert.Equal("text", _db.ExecuteScalar<string>("SELECT typeof(n) FROM loose"));
    }

    /// <summary>
    /// research §4: every JSON column is <c>CHECK (json_valid(col))</c>. Malformed JSON, the empty string and
    /// JSON5 (which json_patch and json_extract would otherwise happily read) are all rejected with
    /// SQLITE_CONSTRAINT_CHECK.
    /// </summary>
    [Theory]
    [InlineData("{bad")]
    [InlineData("")]
    [InlineData("{\"a\":1")]
    [InlineData("{a:1}")]
    [InlineData("{'a':1}")]
    public void JsonValidCheck_InvalidJson_IsRejected(string value)
    {
        var exception = Assert.Throws<SqliteException>(() => _db.Execute("INSERT INTO doc(data) VALUES (@value)", new { value }));

        Assert.Equal(SqliteConstraint, exception.SqliteErrorCode);
        Assert.Equal(SqliteConstraintCheck, exception.SqliteExtendedErrorCode);
    }

    /// <summary>
    /// Where json_valid is weaker than the design assumes. Any JSON value passes, so an array, a number or a
    /// bare string is accepted into a column whose readers expect an object. A later json_patch onto a
    /// non-object target just replaces it. If a column must hold an object, the CHECK has to say so (next test).
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    [InlineData("123")]
    [InlineData("\"x\"")]
    [InlineData("null")]
    public void JsonValidCheck_AnyJsonValue_IsAccepted(string value)
    {
        _db.Execute("INSERT INTO doc(data) VALUES (@value)", new { value });

        Assert.Equal(value, _db.ExecuteScalar<string>("SELECT data FROM doc"));
    }

    /// <summary>
    /// The object-only form <c>CHECK (json_valid(data) AND json_type(data) = 'object')</c> works. The
    /// json_valid half is needed: json_type on malformed text raises "malformed JSON" (a plain error, not a
    /// constraint failure), and AND short-circuits before json_type runs.
    /// </summary>
    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"attitude\":10}", true)]
    [InlineData("[1]", false)]
    [InlineData("123", false)]
    [InlineData("{bad", false)]
    [InlineData("", false)]
    public void ObjectCheck_JsonValidAndJsonTypeObject_AcceptsOnlyObjects(string value, bool accepted)
    {
        var exception = Record.Exception(() => _db.Execute("INSERT INTO object_doc(data) VALUES (@value)", new { value }));

        if (accepted)
        {
            Assert.Null(exception);
        }
        else
        {
            var sqlite = Assert.IsType<SqliteException>(exception);
            Assert.Equal(SqliteConstraintCheck, sqlite.SqliteExtendedErrorCode);
        }
    }
}
