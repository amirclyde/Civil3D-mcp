using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

public static class CivilExecution
{
  private static readonly SemaphoreSlim HostExecutionGate = new(1, 1);

  public static async Task<T> ExecuteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write)
  {
    return await ExecuteSerializedAsync(async () =>
    {
      T? result = default;
      Exception? capturedException = null;

      // Completion is signalled by the delegate itself. Awaiting only the Task returned by
      // ExecuteInCommandContextAsync hung forever on Civil 3D 2026.2 when the work inside
      // triggered a corridor rebuild that built a corridor surface: the delegate finished and the
      // transaction committed, but AutoCAD's command-context task never completed, which left the
      // execution gate held and every later request timing out.
      var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      var abandoned = 0;

      var commandTask = AsTask(App.DocumentManager.ExecuteInCommandContextAsync(async _ =>
      {
        if (Volatile.Read(ref abandoned) == 1) { finished.TrySetResult(true); return; }
        started.TrySetResult(true);
        try
        {
          var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
          var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();
          var activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
          if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
              !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.CONFLICT",
              $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. No drawing changes were made.");
          }
          var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
          var database = doc.Database;

          using var documentLock = doc.LockDocument();
          using var transaction = database.TransactionManager.StartTransaction();

          result = action(doc, civilDoc, database, transaction);

          if (write)
          {
            transaction.Commit();
          }
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }
        finally
        {
          finished.TrySetResult(true);
        }

        await Task.CompletedTask;
      }, null));

      await AwaitCommandContextAsync(commandTask, finished, started, () => Volatile.Write(ref abandoned, 1));

      if (capturedException != null)
      {
        throw capturedException;
      }

      return result!;
    });
  }

  /// <summary>
  /// Waits for the delegate's own completion signal. The AutoCAD command-context task normally
  /// completes at the same time; when it lingers (see ExecuteAsync) we log it and carry on, and
  /// observe its eventual fault so nothing is left unobserved.
  /// </summary>
  /// <summary>ExecuteInCommandContextAsync returns AutoCAD's own awaitable (DocumentCollection.ExecutionResult); wrap it in a Task.</summary>
  private static async Task AsTask(DocumentCollection.ExecutionResult execution)
  {
    await execution;
  }

  /// <summary>
  /// How long to wait for AutoCAD to actually enter the command context. When a modal dialog is up,
  /// the drawing is being switched/recovered, or the request was queued on a document that is no
  /// longer active, the delegate never starts; without this the execution gate stayed held forever
  /// and every later request timed out until Civil 3D was restarted.
  /// </summary>
  private static readonly TimeSpan CommandContextStartTimeout = TimeSpan.FromSeconds(90);

  private static async Task AwaitCommandContextAsync(Task commandTask, TaskCompletionSource<bool> finished, TaskCompletionSource<bool> started, Action abandon)
  {
    await Task.WhenAny(started.Task, finished.Task, commandTask, Task.Delay(CommandContextStartTimeout));
    if (!started.Task.IsCompleted && !finished.Task.IsCompleted && !commandTask.IsCompleted)
    {
      abandon();
      PluginLog.Warn("CivilExecution", $"Command context did not start within {CommandContextStartTimeout.TotalSeconds:0} s; request abandoned (nothing was run).", null);
      _ = commandTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
      throw new JsonRpcDispatchException(
        "CIVIL3D.BUSY",
        $"Civil 3D did not enter a command context within {CommandContextStartTimeout.TotalSeconds:0} s (a dialog may be open, a drawing may be loading or switching, or AutoCAD is busy). Nothing was run; retry when Civil 3D is idle.");
    }

    var first = await Task.WhenAny(finished.Task, commandTask);
    if (first == commandTask && commandTask.IsFaulted)
    {
      // The delegate could not be scheduled at all (no command context available).
      throw commandTask.Exception?.GetBaseException() ?? new InvalidOperationException("ExecuteInCommandContextAsync faulted.");
    }
    if (!finished.Task.IsCompleted)
    {
      // Command context ended before the delegate reported completion (should not happen); wait briefly.
      await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    }
    if (!commandTask.IsCompleted)
    {
      // Normal case: the delegate has just returned and AutoCAD ends the command context a moment later.
      await Task.WhenAny(commandTask, Task.Delay(TimeSpan.FromSeconds(30)));
    }
    if (!commandTask.IsCompleted)
    {
      PluginLog.Warn("CivilExecution", "Delegate completed but AutoCAD's command-context task is still pending; continuing without it.", null);
      _ = commandTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
  }

  public static async Task<T> ExecuteInCommandContextAsync<T>(Func<Task<T>> action)
  {
    return await ExecuteSerializedAsync(async () =>
    {
      T? result = default;
      Exception? capturedException = null;
      var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      var abandoned = 0;

      var commandTask = AsTask(App.DocumentManager.ExecuteInCommandContextAsync(async _ =>
      {
        if (Volatile.Read(ref abandoned) == 1) { finished.TrySetResult(true); return; }
        started.TrySetResult(true);
        try
        {
          result = await action();
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }
        finally
        {
          finished.TrySetResult(true);
        }
      }, null));

      await AwaitCommandContextAsync(commandTask, finished, started, () => Volatile.Write(ref abandoned, 1));

      if (capturedException != null)
      {
        throw capturedException;
      }

      return result!;
    });
  }

  /// <summary>
  /// Runs the action on AutoCAD's main thread in application context, via Application.Idle, with an
  /// explicit document lock and transaction. Used for corridor-surface work: on Civil 3D 2026.2 a
  /// command context in which a corridor surface is added never ends (blocking every later
  /// command-context call), and DocumentCollection.ExecuteInApplicationContext called from the
  /// plugin's RPC thread ran the callback on that thread (Application.OnIdle then crashed AutoCAD's
  /// WPF layout tabs with a cross-thread access exception). Application.Idle is raised on the main
  /// thread, so the work runs where Civil 3D expects it.
  /// </summary>
  public static async Task<T> ExecuteInApplicationContextAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write)
  {
    return await ExecuteSerializedAsync(async () =>
    {
      var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
      EventHandler? handler = null;
      handler = (sender, e) =>
      {
        App.Idle -= handler;
        try
        {
          var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
          var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();
          var activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
          if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
              !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.CONFLICT",
              $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. No drawing changes were made.");
          }
          var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
          var database = doc.Database;

          T result;
          using (var documentLock = doc.LockDocument())
          using (var transaction = database.TransactionManager.StartTransaction())
          {
            result = action(doc, civilDoc, database, transaction);
            if (write)
            {
              transaction.Commit();
            }
          }
          completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
          completion.TrySetException(ex);
        }
      };
      App.Idle += handler;

      var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
      using (cancellationToken.Register(() =>
      {
        App.Idle -= handler;
        completion.TrySetCanceled(cancellationToken);
      }))
      {
        return await completion.Task;
      }
    });
  }

  public static Task<T> WriteInApplicationContextAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteInApplicationContextAsync(action, true);
  }

  // ─── Real-command execution (SendStringToExecute → C3DMCPRUNQUEUED) ───

  private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> QueuedWork = new();

  /// <summary>
  /// Runs the action inside a genuine AutoCAD command: the work item is queued, the
  /// C3DMCPRUNQUEUED command is posted from Application.Idle (main thread), and the command body
  /// executes the action with a transaction. This gives Civil 3D the same command plumbing its
  /// own dialogs have — required for corridor surfaces on 2026.2 (see RunQueuedCommand).
  /// </summary>
  public static async Task<T> ExecuteAsCommandAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write, bool wrapInTransaction = true)
  {
    return await ExecuteSerializedAsync(async () =>
    {
      var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
      var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      var abandoned = 0;
      QueuedWork.Enqueue(() =>
      {
        if (Volatile.Read(ref abandoned) == 1) return; // request already timed out; never run stale work
        started.TrySetResult(true);
        try
        {
          var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
          var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();
          var activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
          if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
              !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.CONFLICT",
              $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. No drawing changes were made.");
          }
          var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
          var database = doc.Database;

          T result;
          if (wrapInTransaction)
          {
            using var transaction = database.TransactionManager.StartTransaction();
            result = action(doc, civilDoc, database, transaction);
            if (write)
            {
              transaction.Commit();
            }
          }
          else
          {
            // e.g. QSAVE: must not run inside an open transaction
            result = action(doc, civilDoc, database, null!);
          }
          completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
          completion.TrySetException(ex);
        }
      });

      EventHandler? handler = null;
      handler = (sender, e) =>
      {
        App.Idle -= handler;
        try
        {
          var doc = App.DocumentManager.MdiActiveDocument;
          if (doc == null)
          {
            completion.TrySetException(new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D."));
            return;
          }
          doc.SendStringToExecute("_.C3DMCPRUNQUEUED ", true, false, false);
        }
        catch (Exception ex)
        {
          completion.TrySetException(ex);
        }
      };
      App.Idle += handler;

      var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
      using (cancellationToken.Register(() =>
      {
        App.Idle -= handler;
        Volatile.Write(ref abandoned, 1);
        completion.TrySetCanceled(cancellationToken);
      }))
      {
        await Task.WhenAny(started.Task, completion.Task, Task.Delay(CommandContextStartTimeout));
        if (!started.Task.IsCompleted && !completion.Task.IsCompleted)
        {
          Volatile.Write(ref abandoned, 1);
          App.Idle -= handler;
          PluginLog.Warn("CivilExecution", $"C3DMCPRUNQUEUED did not start within {CommandContextStartTimeout.TotalSeconds:0} s; request abandoned (nothing was run).", null);
          throw new JsonRpcDispatchException(
            "CIVIL3D.BUSY",
            $"Civil 3D did not run the queued command within {CommandContextStartTimeout.TotalSeconds:0} s (a dialog may be open, a drawing may be loading or switching, or AutoCAD is busy). Nothing was run; retry when Civil 3D is idle.");
        }
        return await completion.Task;
      }
    });
  }

  public static Task<T> WriteAsCommandAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsCommandAsync(action, true);
  }

  /// <summary>Body of the C3DMCPRUNQUEUED command.</summary>
  public static void RunQueuedWorkItem()
  {
    if (QueuedWork.TryDequeue(out var work))
    {
      work();
    }
    else
    {
      PluginLog.Warn("CivilExecution", "C3DMCPRUNQUEUED ran with nothing queued.", null);
    }
  }

  public static Task<T> ReadAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, false);
  }

  public static Task<T> WriteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, true);
  }

  private static async Task<T> ExecuteSerializedAsync<T>(Func<Task<T>> action)
  {
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    PluginRuntime.QueueHostOperation();
    var started = false;

    try
    {
      await HostExecutionGate.WaitAsync(cancellationToken);
      started = true;
      PluginRuntime.StartHostOperation();
      cancellationToken.ThrowIfCancellationRequested();
      return await action();
    }
    finally
    {
      if (started)
      {
        PluginRuntime.CompleteHostOperation();
        HostExecutionGate.Release();
      }
      else
      {
        PluginRuntime.CancelQueuedHostOperation();
      }
    }
  }
}
