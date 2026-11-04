using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace CloudyWing.AiAccountManager.Components.Tests;

/// <summary>
/// 以使用者看得到的文字或無障礙名稱尋找元素，避免測試綁定 MudBlazor 內部的 CSS 類別。
/// </summary>
internal static class ElementQueries {
    /// <summary>
    /// 尋找文字完全相符的按鈕。
    /// </summary>
    public static IElement FindButton<TComponent>(this IRenderedComponent<TComponent> cut, string text)
        where TComponent : IComponent {
        IElement[] matches = cut.FindAll("button")
            .Where(button => string.Equals(button.TextContent.Trim(), text, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException($"預期找到 1 個文字為「{text}」的按鈕，實際找到 {matches.Length} 個。");
    }

    /// <summary>
    /// 判斷是否存在文字完全相符的按鈕。
    /// </summary>
    public static bool HasButton<TComponent>(this IRenderedComponent<TComponent> cut, string text)
        where TComponent : IComponent {
        return cut.FindAll("button").Any(button => string.Equals(button.TextContent.Trim(), text, StringComparison.Ordinal));
    }

    /// <summary>
    /// 尋找 aria-label 完全相符的元素。
    /// </summary>
    public static IElement FindByLabel<TComponent>(this IRenderedComponent<TComponent> cut, string label)
        where TComponent : IComponent {
        return cut.FindAll("[aria-label]")
            .Single(element => string.Equals(element.GetAttribute("aria-label"), label, StringComparison.Ordinal));
    }

    /// <summary>
    /// 判斷元素是否為停用狀態。
    /// </summary>
    public static bool IsDisabled(this IElement element) => element.HasAttribute("disabled");
}
