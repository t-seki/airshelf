namespace Radirec.Core.Jellyfin;

/// <summary>
/// Jellyfin API 呼び出しの失敗。または設定の不正で呼び出せない場合
/// (JellyfinUrl の形式不正。<see cref="MisconfiguredLibraryRefresher"/>)。
/// </summary>
public sealed class JellyfinException(string message) : Exception(message);
