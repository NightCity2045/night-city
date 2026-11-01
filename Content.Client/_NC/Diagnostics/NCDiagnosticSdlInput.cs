// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#if TOOLS && NC_DIAGNOSTICS
using System.Runtime.InteropServices;
using System.Threading;
using NightCity.Diagnostics.Contracts;

namespace Content.Client._NC.Diagnostics;

/// <summary>
/// Uses SDL3's published event ABI to feed this process's own UI input path. It neither moves the desktop
/// pointer nor invokes private Robust methods. SDL API reference: https://wiki.libsdl.org/SDL3/SDL_PushEvent
/// </summary>
internal static class NCDiagnosticSdlInput
{
    private sealed record ClickWork(string Title, float X, float Y, Action<DiagnosticFault?> Completed, bool Press);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MainThreadCallback(nint userData);
    private static readonly MainThreadCallback Callback = OnMainThread;
    private static int _pending;

    public static void Click(string windowTitle, float pixelX, float pixelY, Action<DiagnosticFault?> completed, bool press = true)
    {
        if (Interlocked.Increment(ref _pending) > 8)
        {
            Interlocked.Decrement(ref _pending);
            throw new DiagnosticException("busy", "The native UI input queue is full.", true);
        }
        var handle = GCHandle.Alloc(new ClickWork(windowTitle, pixelX, pixelY, completed, press));
        try
        {
            // SDL window queries must run on SDL's thread, which may differ from the game thread.
            // Never synchronously wait: the SDL and game threads may already be exchanging window commands.
            if (!RunOnMainThread(Callback, GCHandle.ToIntPtr(handle), false))
            {
                handle.Free();
                Interlocked.Decrement(ref _pending);
                throw new DiagnosticException("input_unavailable", "SDL rejected the queued UI callback.");
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            handle.Free();
            Interlocked.Decrement(ref _pending);
            throw new DiagnosticException("input_backend_unsupported", "The SDL3 main-thread dispatch API is unavailable.");
        }
    }

    private static void OnMainThread(nint userData)
    {
        var handle = GCHandle.FromIntPtr(userData);
        try
        {
            var work = (ClickWork)handle.Target!;
            try
            {
                PushClick(work.Title, work.X, work.Y, work.Press);
                work.Completed(null);
            }
            catch (DiagnosticException exception)
            {
                work.Completed(new DiagnosticFault(exception.Code, exception.Message, exception.Retryable));
            }
            catch (Exception)
            {
                work.Completed(new DiagnosticFault("input_failed", "The queued UI input could not be delivered."));
            }
        }
        catch (Exception)
        {
            // An exception must never cross the unmanaged SDL callback boundary.
        }
        finally
        {
            handle.Free();
            Interlocked.Decrement(ref _pending);
        }
    }
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct InputEvent
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(16)] public uint WindowId;
        [FieldOffset(24)] public byte Button;
        [FieldOffset(25)] public byte Down;
        [FieldOffset(26)] public byte Clicks;
        [FieldOffset(28)] public float X;
        [FieldOffset(32)] public float Y;
    }

    private static void PushClick(string windowTitle, float pixelX, float pixelY, bool press)
    {
        try
        {
            var windows = GetWindows(out var count);
            if (windows == 0)
                throw new DiagnosticException("input_unavailable", "SDL has no graphical window.");
            nint selected = count == 1 ? Marshal.ReadIntPtr(windows) : 0;
            var matchingTitles = 0;
            try
            {
                for (var index = 0; index < Math.Min(count, 32); index++)
                {
                    var window = Marshal.ReadIntPtr(windows, index * IntPtr.Size);
                    if (Marshal.PtrToStringUTF8(GetWindowTitle(window)) == windowTitle)
                    {
                        selected = window;
                        matchingTitles++;
                    }
                }
            }
            finally { Free(windows); }
            if (count > 1 && matchingTitles != 1)
                throw new DiagnosticException("input_window_ambiguous", "The process has multiple graphical windows and no unique matching main-window title.");
            if (selected == 0 || !GetWindowSize(selected, out var width, out var height) ||
                !GetWindowSizeInPixels(selected, out var pixelWidth, out var pixelHeight) || pixelWidth <= 0 || pixelHeight <= 0)
                throw new DiagnosticException("input_unavailable", "The selected graphical window is unavailable.");
            var x = pixelX * width / pixelWidth;
            var y = pixelY * height / pixelHeight;
            var windowId = GetWindowId(selected);
            var motion = new InputEvent { Type = 0x400, WindowId = windowId, X = x, Y = y };
            var down = new InputEvent { Type = 0x401, WindowId = windowId, X = x, Y = y, Button = 1, Down = 1, Clicks = 1 };
            var up = new InputEvent { Type = 0x402, WindowId = windowId, X = x, Y = y, Button = 1, Clicks = 1 };
            if (!PushEvent(ref motion))
                throw new DiagnosticException("input_unavailable", "SDL rejected the UI pointer event.");
            if (!press) return;
            if (!PushEvent(ref down))
                throw new DiagnosticException("input_unavailable", "SDL rejected the UI input event.");
            if (!PushEvent(ref up))
            {
                PushEvent(ref up);
                throw new DiagnosticException("outcome_unknown", "SDL accepted mouse-down but did not confirm mouse-up.");
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new DiagnosticException("input_backend_unsupported", "The current renderer does not expose the supported SDL3 event API.");
        }
    }

    [DllImport("SDL3", EntryPoint = "SDL_GetWindows", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint GetWindows(out int count);
    [DllImport("SDL3", EntryPoint = "SDL_RunOnMainThread", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool RunOnMainThread(MainThreadCallback callback, nint userData, [MarshalAs(UnmanagedType.I1)] bool waitComplete);
    [DllImport("SDL3", EntryPoint = "SDL_GetWindowTitle", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint GetWindowTitle(nint window);
    [DllImport("SDL3", EntryPoint = "SDL_GetWindowID", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint GetWindowId(nint window);
    [DllImport("SDL3", EntryPoint = "SDL_GetWindowSize", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetWindowSize(nint window, out int width, out int height);
    [DllImport("SDL3", EntryPoint = "SDL_GetWindowSizeInPixels", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GetWindowSizeInPixels(nint window, out int width, out int height);
    [DllImport("SDL3", EntryPoint = "SDL_PushEvent", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool PushEvent(ref InputEvent ev);
    [DllImport("SDL3", EntryPoint = "SDL_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Free(nint pointer);
}

#endif
