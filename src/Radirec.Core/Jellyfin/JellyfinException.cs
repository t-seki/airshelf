namespace Radirec.Core.Jellyfin;

/// <summary>Jellyfin API 呼び出しの失敗。</summary>
public sealed class JellyfinException(string message) : Exception(message);
