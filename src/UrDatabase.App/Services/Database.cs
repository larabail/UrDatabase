using System.IO;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    public static class Database
    {
        public static SqliteConnection Open(string dbPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
            return conn;
        }
    }
}
