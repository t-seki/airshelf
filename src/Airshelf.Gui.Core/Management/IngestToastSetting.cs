namespace Airshelf.Gui.Core.Management;

/// <summary>
/// 「新規取込のトーストを出す」(W-302・#152・ADR-0019)。値は管理画面用の設定ファイル(<see cref="GuiSettingsStore"/>)に持ち、
/// 取り込みワーカーのスレッドがトーストを出す瞬間に <see cref="Enabled"/> を読む(切り替えはリロード無しですぐ効く)。
/// 取込失敗のトーストと失敗のストアは、この設定に関係なく常に動く。
/// <para>
/// 読み書きのスレッド: <see cref="Enabled"/> はワーカーのスレッドからも読まれるので volatile で持つ。
/// <see cref="Set"/> は UI スレッドから呼ぶ。
/// </para>
/// </summary>
public sealed class IngestToastSetting
{
    private readonly GuiSettingsStore? _store;
    private volatile bool _enabled;

    private IngestToastSetting(GuiSettingsStore? store, bool enabled)
    {
        _store = store;
        _enabled = enabled;
    }

    /// <summary>新規取込のトーストを出すか。</summary>
    public bool Enabled => _enabled;

    /// <summary>保存しない固定の設定(テストと、設定ファイルを使えないとき用)。</summary>
    public static IngestToastSetting Fixed(bool enabled) => new(null, enabled);

    /// <summary>
    /// 起動時に読む。設定ファイルに値があればそれを使う。無ければ <paramref name="legacyNotificationsEnabled"/>
    /// (<c>airshelf.json</c> の旧設定 <c>NotificationsEnabled</c>)を初期値にして設定ファイルへ書く(以後は設定ファイルの値だけを使う)。
    /// <c>airshelf.json</c> を読めなかった(<paramref name="legacyNotificationsEnabled"/> が null)ときは、書かずに「出す」で始める
    /// (旧設定が分からないうちに値を固めない。次に読めた起動で引き継ぐ)。
    /// </summary>
    /// <param name="logWarn">gui.log への記録(初期値を書けなかったとき)。</param>
    public static IngestToastSetting Load(GuiSettingsStore store, bool? legacyNotificationsEnabled, Action<string> logWarn)
    {
        var settings = store.Load();
        if (settings.IngestToastsEnabled is { } stored)
            return new IngestToastSetting(store, stored);

        if (legacyNotificationsEnabled is not { } legacy)
            return new IngestToastSetting(store, true);

        try
        {
            store.Save(settings with { IngestToastsEnabled = legacy });
        }
        catch (Exception ex)
        {
            // 書けなくても今回の起動はこの値で動く(次の起動でもう一度引き継ぐ)。
            logWarn($"{GuiSettingsStore.FileName} に初期値を書けませんでした({store.Path}): {ex.Message}");
        }
        return new IngestToastSetting(store, legacy);
    }

    /// <summary>
    /// 切り替えて保存する。保存に失敗したら例外を投げ、値は変えない(画面は元の値に戻す)。
    /// </summary>
    public void Set(bool enabled)
    {
        if (_store is not null)
            _store.Save(_store.Load() with { IngestToastsEnabled = enabled });
        _enabled = enabled;
    }
}
