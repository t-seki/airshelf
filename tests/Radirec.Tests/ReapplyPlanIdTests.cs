using Radirec.Core.Reapply;

namespace Radirec.Tests;

/// <summary>予定 ID(CONTEXT.md。#181)。再適用の dry-run の明細から、中身で一意に決まる。</summary>
public sealed class ReapplyPlanIdTests
{
    private static ReapplyItem Item(string from, ReapplyAction action = ReapplyAction.Move) => new()
    {
        Action = action,
        Station = "TBSラジオ",
        FromAlbum = "安住紳一郎の日曜天国(1)",
        ToAlbum = "安住紳一郎の日曜天国",
        BroadcastDate = new DateOnly(2026, 9, 20),
        BroadcastTime = new TimeOnly(10, 0),
        FromPath = from,
        ToPath = @"Z:\out\TBSラジオ\安住紳一郎の日曜天国\2026-09-20.m4a",
        SourcePath = @"I:\radio\a.m4a",
    };

    [Fact]
    public void 同じ予定なら同じIDで_16文字の16進()
    {
        var a = ReapplyPlanId.Of([Item(@"Z:\a.m4a"), Item(@"Z:\b.m4a")]);
        var b = ReapplyPlanId.Of([Item(@"Z:\a.m4a"), Item(@"Z:\b.m4a")]);

        Assert.Equal(a, b);
        Assert.Equal(ReapplyPlanId.Length, a.Length);
        Assert.True(ReapplyPlanId.IsWellFormed(a));
        Assert.Equal(a.ToLowerInvariant(), a);
    }

    [Fact]
    public void 並び順に依らない()
    {
        Assert.Equal(
            ReapplyPlanId.Of([Item(@"Z:\a.m4a"), Item(@"Z:\b.m4a")]),
            ReapplyPlanId.Of([Item(@"Z:\b.m4a"), Item(@"Z:\a.m4a")]));
    }

    [Fact]
    public void 一回でも違えば違うID()
    {
        var baseline = ReapplyPlanId.Of([Item(@"Z:\a.m4a"), Item(@"Z:\b.m4a")]);

        Assert.NotEqual(baseline, ReapplyPlanId.Of([Item(@"Z:\a.m4a")]));
        Assert.NotEqual(baseline, ReapplyPlanId.Of([Item(@"Z:\a.m4a"), Item(@"Z:\c.m4a")]));
        Assert.NotEqual(baseline, ReapplyPlanId.Of([Item(@"Z:\a.m4a"), Item(@"Z:\b.m4a", ReapplyAction.Excluded)]));
        Assert.NotEqual(baseline, ReapplyPlanId.Of([Item(@"Z:\a.m4a"), Item(@"Z:\b.m4a") with { Reason = "重複" }]));
        Assert.NotEqual(baseline, ReapplyPlanId.Of([Item(@"Z:\a.m4a"), Item(@"Z:\b.m4a") with { RewritesTags = true }]));
        Assert.NotEqual(baseline, ReapplyPlanId.Of([Item(@"Z:\a.m4a"), Item(@"Z:\b.m4a") with { BroadcastTime = null }]));
    }

    [Fact]
    public void 値の区切りを取り違えない()
    {
        // 「放送局 + アルバム」の境目がずれただけの 2 つを、別の予定として区別する。
        var a = Item(@"Z:\a.m4a") with { Station = "AB", FromAlbum = "C" };
        var b = Item(@"Z:\a.m4a") with { Station = "A", FromAlbum = "BC" };

        Assert.NotEqual(ReapplyPlanId.Of([a]), ReapplyPlanId.Of([b]));
    }

    [Fact]
    public void 変える回が無い予定にもIDがある()
    {
        var empty = ReapplyPlanId.Of([]);

        Assert.True(ReapplyPlanId.IsWellFormed(empty));
        Assert.Equal(empty, ReapplyPlanId.Of(new ReapplyReport { Items = [], DryRun = true, UnintegratedSiblings = [] }));
    }

    [Fact]
    public void 兄弟フォルダの知らせはIDに入れない()
    {
        var items = new[] { Item(@"Z:\a.m4a") };
        var plain = new ReapplyReport { Items = items, DryRun = true, UnintegratedSiblings = [] };
        var withSiblings = plain with
        {
            UnintegratedSiblings = [new UnintegratedSiblingGroup(new("TBSラジオ", "a", ["a(1)", "a(2)"]), ["a(2)"])],
        };

        Assert.Equal(ReapplyPlanId.Of(plain), ReapplyPlanId.Of(withSiblings));
    }

    [Theory]
    [InlineData("3f9c0a1b2d4e5f67", true)]
    [InlineData("3F9C0A1B2D4E5F67", true)]
    [InlineData("3f9c0a1b2d4e5f6", false)]
    [InlineData("3f9c0a1b2d4e5f678", false)]
    [InlineData("3f9c0a1b2d4e5f6g", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IDの形(string? id, bool wellFormed) => Assert.Equal(wellFormed, ReapplyPlanId.IsWellFormed(id));

    [Fact]
    public void 明細の項目が増えたら予定IDの材料にも足す()
    {
        // ReapplyPlanId.Canonical は ReapplyItem の全項目を材料にする(HasSamePlanAs と同じ)。項目を足したら、そちらにも足してこの数を直す。
        Assert.Equal(11, typeof(ReapplyItem).GetProperties().Count(p => p.Name != "EqualityContract"));
    }
}
