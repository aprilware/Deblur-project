# Deblur — Phase 5e Design (Full-res Render Cancellation)

**Date:** 2026-07-09
**Status:** Approved
**Scope:** Fifth mini-phase of phase 5. Cancellation of the full-res render/save path only.

## Context

Phases 1-4 delivered the deconvolution pipeline; phase 5a added zoom+pan, 5b keyboard shortcuts. The remaining phase-5 mini-phases are 5c (undo), 5d (batch), 5e (cancellation), plus the deferred phase-4b Total Variation. This spec covers 5e: threading a `CancellationToken` through the Save / Render-full path so the busy overlay can offer a Cancel button when a long-running full-res deconvolution is in flight.

## Goal

While the busy overlay is up during Render full resolution / Save As, a Cancel button lets the user abort the in-flight `RenderFullAsync` cleanly. On cancel, the overlay closes, `StatusMessage` reads "Cancelled", the file is NOT written, and the app remains usable. No engine algorithm changes.

## Non-goals

- Cancellation of the proxy-preview worker (already coalesces; sub-second turnaround at 400 KP).
- Undo of already-saved output.
- Any of the other phase-5 mini-phases (5c undo, 5d batch, 4b TV).
- Persistent cancel state or partial results.

## Approach

Add an optional `CancellationToken` parameter to `DeblurJobRunner.RenderFullAsync` and the three `MainViewModel.RenderFull*Async` / `EnsureFullResRenderedAsync` methods. The runner checks the token before each `progress?.Report` boundary (there are already three: 0.1, 0.3, 1.0). `BusyOverlay` gains a Cancel `Button` that fires a new `CancelRequested` routed event and a public `SetCancellable(bool)` method so `MainWindow` can show/hide the button per operation. `MainWindow`'s Save and Render handlers create a `CancellationTokenSource`, wire `Busy.CancelRequested` to `cts.Cancel()`, pass `cts.Token`, and dispose the CTS in `finally`. `OperationCanceledException` is caught in the handlers and sets `Vm.StatusMessage = "Cancelled"` — no error MessageBox.

## Files touched

- `Deblur.Engine/DeblurJobRunner.cs` — add `CancellationToken` param to `RenderFullAsync` and check at the three progress boundaries.
- `Deblur/ViewModels/MainViewModel.cs` — thread the token through `EnsureFullResRenderedAsync`, `RenderFullAsPngAsync`, `RenderFullAsJpegAsync`.
- `Deblur/Controls/BusyOverlay.xaml` + `.cs` — add Cancel `Button` (visibility bound to a new `IsCancellable` state), `CancelRequested` routed event, `SetCancellable(bool)` method.
- `Deblur/MainWindow.xaml.cs` — Save + Render handlers create/dispose CTS, subscribe to `Busy.CancelRequested`, catch `OperationCanceledException`.
- `Deblur.Tests/DeblurJobRunnerTests.cs` — one new test asserts `RenderFullAsync` throws `OperationCanceledException` when the token is pre-cancelled.

## Constraints

- .NET 8. `net8.0-windows` WPF, `net8.0` Engine + Tests. Nullable + ImplicitUsings enabled everywhere.
- No new NuGet packages. `System.Threading.CancellationToken` is in the BCL.
- All 53 phase-5b tests remain green; the new test brings total to 54.
- Existing behavior of Save/Render (busy overlay, IsBusy toggle, error paths, progress bar) unchanged when the user doesn't press Cancel.
- The Cancel button is HIDDEN by default (`IsCancellable=false`) and shown only when `MainWindow` calls `Busy.SetCancellable(true)`. `BusyOverlay.Hide()` also resets it to false.
- Cancellation triggered by clicking Cancel OR pressing Esc while the modal is up (Cancel button `IsCancel="True"` handles Esc automatically inside the overlay's own key routing — verified in smoke).

## Testing

Engine: one new test on `DeblurJobRunner.RenderFullAsync` with a pre-cancelled `CancellationToken` — asserts `OperationCanceledException`. No new WPF unit tests.

Manual smoke:
- Load a moderately large image (large enough that full-res Wiener takes >2 s).
- Click Render full resolution → busy overlay appears with Cancel button. Wait a beat and click Cancel — overlay closes, status shows "Cancelled".
- Click Render full resolution again, don't cancel — normal completion; status shows "Full-resolution render ready".
- File → Save As → PNG → Cancel mid-render — file is NOT written; status shows "Cancelled".
- Save As → PNG normally — file saved as before.
- Esc while overlay is up (during a render) cancels it, same as Cancel button.

## Branch

Phase 5e branches from tag `phase5b` onto `phase5e-cancellation`.
