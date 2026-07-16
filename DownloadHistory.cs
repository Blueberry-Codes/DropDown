using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DropDown;

/// <summary>One completed download, persisted to %LOCALAPPDATA%\DropDown\history.json.</summary>
public sealed class DownloadHistoryEntry
{
    public string FileName { get; set; }
    public string FullPath { get; set; }
    public string SourceUrl { get; set; }
    public string InstanceApi { get; set; }
    public long SizeBytes { get; set; }
    public DateTime TimestampUtc { get; set; }
}

/// <summary>Persisted list of completed downloads, newest first, capped at MaxEntries.</summary>
public sealed class DownloadHistory
{
    public const int MaxEntries = 50;

    public List<DownloadHistoryEntry> Entries { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DropDown", "history.json");

    public static DownloadHistory Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var history = JsonSerializer.Deserialize<DownloadHistory>(File.ReadAllText(FilePath)) ?? new DownloadHistory();
                history.Entries ??= new List<DownloadHistoryEntry>();
                return history;
            }
        }
        catch
        {
            // Corrupt or unreadable history: fall back to an empty list rather than crash
        }
        return new DownloadHistory();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // History is a convenience; never let a save failure break the app
        }
    }

    public void AddEntry(DownloadHistoryEntry entry)
    {
        Entries.Insert(0, entry);
        if (Entries.Count > MaxEntries)
        {
            Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
        }
    }
}
