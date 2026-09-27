namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// The rules index cannot be built or used, for a reason a person can fix: the vendored content is missing, damaged or
/// does not match its manifest, the Rules Glossary is missing or unreadable, a record lacks the fields every record
/// must have, or the srd.db an open <see cref="SrdIndex"/> reads became unreadable under it (damaged or overwritten in
/// place; deleting srd.db or renaming another build over it does not do this, because the index's connections keep the
/// file they validated).
///
/// <para>
/// A distinct type so the host can tell these apart from bugs. The message is written for whoever reads the tool
/// error or the stderr log: it names the file (and the record position where there is one) and says what to do,
/// usually "re-vendor with scripts/fetch-5e-database.sh". The host shows it instead of the SDK's bare
/// "An error occurred invoking …", which would leave rules lookup broken with no explanation. Thrown by a query on an
/// open index, it also tells the host to drop that index and open (or rebuild) a current one for the next call, which
/// is what its message promises. Any other exception escaping the builder or the index is a bug and should be treated
/// as one.
/// </para>
/// </summary>
public sealed class SrdIndexUnavailableException : Exception
{
    public SrdIndexUnavailableException(string message)
        : base(message)
    {
    }

    public SrdIndexUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
