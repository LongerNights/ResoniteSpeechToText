using System;
using System.Collections.Generic;
using System.Reflection;
using Elements.Core;
using FrooxEngine;

namespace SpeechToText;

/// <summary>
/// Dash toasts for things the user should know (SPEC §6.6, M7): model downloads, readiness, failures, the one-time privacy
/// notice. Safe from any thread (<c>NotificationPanel</c> marshals itself). The toast's title line is "Speech to text": the
/// public <c>ShowNotification</c> always titles it "N/A", so the private overload that takes a title is called, with the
/// public call as the fallback if an engine update changes it. Identical messages are not repeated within 20 s.
/// </summary>
internal static class Notify
{
    private const string Title = "Speech to text";

    // NotificationPanel.AddNotification(userId, message, thumbnail, color, type, mainMessage, overrideProfile, clip), private.
    private static readonly MethodInfo? AddNotification = typeof(NotificationPanel).GetMethod(
        "AddNotification",
        BindingFlags.Instance | BindingFlags.NonPublic,
        null,
        new[] { typeof(string), typeof(string), typeof(Uri), typeof(colorX), typeof(NotificationType), typeof(string), typeof(Uri), typeof(IAssetProvider<AudioClip>) },
        null);

    private static readonly Dictionary<string, long> lastShown = new();

    public static void Info(string message) => Show(message, RadiantUI_Constants.Dark.CYAN);
    public static void Warn(string message) => Show(message, RadiantUI_Constants.Dark.YELLOW);
    public static void Error(string message) => Show(message, RadiantUI_Constants.Dark.RED);

    private static void Show(string message, colorX color)
    {
        try
        {
            long now = Environment.TickCount64;
            lock (lastShown)
            {
                if (lastShown.TryGetValue(message, out long at) && now - at < 20_000) return;
                lastShown[message] = now;
            }

            var panel = NotificationPanel.Current;
            if (panel == null) return; // dash not up yet; the log still has it

            if (AddNotification != null)
            {
                panel.RunSynchronously(() =>
                {
                    try { AddNotification.Invoke(panel, new object?[] { null, message, null, color, NotificationType.ToastOnly, Title, null, null }); }
                    catch (Exception e) { SpeechToTextMod.Warn("[notify] toast failed: " + e.GetType().Name); }
                });
                return;
            }
            NotificationPanel.ShowNotification(null!, Title + ": " + message, null!, color, NotificationType.ToastOnly);
        }
        catch (Exception e) { SpeechToTextMod.Warn("[notify] toast failed: " + e.GetType().Name); }
    }
}
