using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class KeyDatabaseService : IKeyDatabaseService
{
    private static readonly string ConnectionString = InitializeConnectionString();
    private static readonly object DatabaseLock = new();

    private static string InitializeConnectionString()
    {
        string folder = AppDataPathHelper.GetAppDataFolder();
        Directory.CreateDirectory(folder);
        string dbPath = Path.Combine(folder, "key_data.db");
        return $"Data Source={dbPath}";
    }

    public KeyDatabaseService()
    {
        lock (DatabaseLock)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"
                CREATE TABLE IF NOT EXISTS KeyPressRecords (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Key TEXT NOT NULL,
                    PressTime TEXT NOT NULL
                );";
            cmd.ExecuteNonQuery();
        }
    }

    public void SaveKeyPress(KeyPressRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        SaveKeyPressBatch(new[] { record });
    }

    public void SaveKeyPressBatch(IReadOnlyCollection<KeyPressRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            return;

        lock (DatabaseLock)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO KeyPressRecords (Key, PressTime) VALUES (@k, @t)";
            var keyParameter = cmd.Parameters.Add("@k", SqliteType.Text);
            var timeParameter = cmd.Parameters.Add("@t", SqliteType.Text);

            foreach (var record in records)
            {
                keyParameter.Value = record.Key;
                timeParameter.Value = record.PressTime.ToString("o");
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public Dictionary<string, int> GetKeyCounts()
    {
        return GetKeyCounts(null, null);
    }

    public Dictionary<string, int> GetKeyCounts(DateTime? from, DateTime? to)
    {
        var counts = new Dictionary<string, int>();
        lock (DatabaseLock)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            var cmd = connection.CreateCommand();

            if (from.HasValue && to.HasValue)
            {
                cmd.CommandText =
                    @"
                    SELECT Key, COUNT(*)
                    FROM KeyPressRecords
                    WHERE PressTime >= @from AND PressTime < @to
                    GROUP BY Key";
                cmd.Parameters.AddWithValue("@from", from.Value.ToString("o"));
                cmd.Parameters.AddWithValue("@to", to.Value.ToString("o"));
            }
            else
            {
                cmd.CommandText = "SELECT Key, COUNT(*) FROM KeyPressRecords GROUP BY Key";
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                counts[reader.GetString(0)] = reader.GetInt32(1);
        }
        return counts;
    }
}
