namespace WindowsMcp.Abstractions.Models;

public record DiskUsageEntry(string Dir, long SizeBytes, string SizeHuman);

public record FileTypeEntry(string Extension, int Count, long SizeBytes, string SizeHuman);

public record StaleFileEntry(string Path, long SizeBytes, string SizeHuman, DateTime Modified);

public record ReclaimableSpace(long TempBytes, long InetCacheBytes, long RecycleBinBytes, long TotalBytes);
