namespace UrDatabase.Models
{
    /// <summary>
    /// The numbers behind the line under the library, kept so the line can be written again.
    /// </summary>
    /// <remarks>
    /// This exists because "Posters present: 648/6000" was computed once, when the library was
    /// read, and then never again. Artwork arrives for minutes afterwards — hours, on a library of
    /// a few thousand — and every one of those posters changed the first number without changing
    /// the sentence, so the one piece of progress the app reports sat still while the work it was
    /// reporting on ran to completion. On a small library it was merely stale; on a large one it
    /// was the whole symptom, because nothing else on the screen says the app is doing anything.
    ///
    /// Only <see cref="LocalWithPosters"/> moves while a library is on screen. The rest are facts
    /// about the read that produced it, which is why they are carried rather than recounted: going
    /// back to the films to re-derive them on every poster would walk the whole catalogue thousands
    /// of times to discover the same five numbers.
    /// </remarks>
    public sealed record LibraryTally(
        int LocalCount,
        int LocalWithPosters,
        int RemoteCount,
        bool HasLocalDatabase,
        string DatabasePath,
        int RemoteSeriesCount)
    {
        /// <summary>The same read, with a fresh count of how much of it has artwork.</summary>
        public LibraryTally WithPosters(int withPosters) => this with { LocalWithPosters = withPosters };

        public string Describe() => Services.LibraryStatus.Describe(
            localCount: LocalCount,
            localWithPosters: LocalWithPosters,
            remoteCount: RemoteCount,
            hasLocalDatabase: HasLocalDatabase,
            databasePath: DatabasePath,
            remoteSeriesCount: RemoteSeriesCount);
    }
}
