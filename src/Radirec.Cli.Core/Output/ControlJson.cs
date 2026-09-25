using Radirec.Hosting.Control;

namespace Radirec.Cli.Core.Output;

/// <summary>
/// <c>status --json</c> の <c>result</c>(#179)。常駐に繋がらなくても答えとして返す(ADR-0020)。
/// </summary>
/// <param name="Reachable">トレイの制御チャネルに繋がったか(繋がったが応答を読めなかったときも true)。</param>
/// <param name="Running">取り込みが動いているか(繋がらない・応答を読めない・縮退・ワーカーが止まったなら false)。</param>
/// <param name="Reason">動いていない理由。動いていれば null。</param>
/// <param name="Tray">トレイが返した状態。繋がらない・応答を読めなければ null。</param>
public sealed record StatusJson(bool Reachable, bool Running, string? Reason, ControlStatus? Tray);

/// <summary><c>reload --json</c> の <c>result</c>(#179)。</summary>
/// <param name="Accepted">設定を検証して受理したか(反映は取り込みの合間に後追いで行う)。</param>
/// <param name="Error">拒否した理由(受理なら null)。</param>
public sealed record ReloadJson(bool Accepted, string? Error);

/// <summary>コマンドへ渡す CLI 自身の版(版が違うときの知らせに使う)。</summary>
public sealed record CliVersion(string Value);
