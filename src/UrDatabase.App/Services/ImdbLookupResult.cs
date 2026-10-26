namespace UrDatabase.Services
{
    /// <summary>What asking for one IMDb rating came to.</summary>
    public enum ImdbLookupStatus
    {
        /// <summary>The title has a rating.</summary>
        Rated,

        /// <summary>The title has none, or OMDb does not know it. An answer, and worth remembering.</summary>
        NoRating,

        /// <summary>OMDb said the day's request limit is reached. Not an answer about the title.</summary>
        LimitReached,

        /// <summary>OMDb refused the key itself. Not an answer about the title either.</summary>
        Refused,

        /// <summary>Anything else that went wrong: the network, a timeout, a server error.</summary>
        Failed
    }

    /// <summary>
    /// One IMDb rating lookup's outcome, with the distinction a bare <c>double?</c> could not
    /// draw: "this title has no rating" is an answer to cache for good, while "OMDb would not say
    /// today" must never be cached, or a rate-limited afternoon would leave every title it touched
    /// without a rating for ever.
    /// </summary>
    public readonly record struct ImdbLookupResult(ImdbLookupStatus Status, double? Rating)
    {
        public static ImdbLookupResult Of(double? rating) =>
            rating is double r ? new(ImdbLookupStatus.Rated, r) : new(ImdbLookupStatus.NoRating, null);

        public static ImdbLookupResult LimitReached { get; } = new(ImdbLookupStatus.LimitReached, null);
        public static ImdbLookupResult Refused { get; } = new(ImdbLookupStatus.Refused, null);
        public static ImdbLookupResult Failed { get; } = new(ImdbLookupStatus.Failed, null);

        /// <summary>True when the result says something about the title and belongs in the cache.</summary>
        public bool IsAnswer => Status is ImdbLookupStatus.Rated or ImdbLookupStatus.NoRating;

        /// <summary>True when no further request is worth making today.</summary>
        public bool StopsForTheDay => Status is ImdbLookupStatus.LimitReached or ImdbLookupStatus.Refused;
    }
}
