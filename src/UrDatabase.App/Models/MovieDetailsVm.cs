using System.Collections.Generic;

namespace UrDatabase.Models
{
    public class MovieDetailsVm
    {
        public long LocalId { get; set; }            // your DB movie id
        public string Title { get; set; } = "";
        public int? Year { get; set; }
        public string Overview { get; set; } = "";
        public string Genres { get; set; } = "";
        public int? Runtime { get; set; }            // minutes
        public double? ImdbRating { get; set; }      // we’ll map TMDb vote_average here for now
        public string? PosterPath { get; set; }      // local or URL (you already have this)
        public string? BackdropUrl { get; set; }     // URL for big backdrop
        public string? FilePath { get; set; }        // first playable file we can find
    }
}
