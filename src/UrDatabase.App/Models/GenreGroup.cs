using System.Collections.ObjectModel;

namespace UrDatabase.Models
{
    public class GenreGroup
    {
        public string Name { get; set; } = "";
        public ObservableCollection<UiMovie> Items { get; set; } = new();
    }
}
