using System;
using System.Collections.Concurrent;
using System.Threading;
using Autodesk.Revit.UI;

namespace McpRevit.Server
{
    /// <summary>
    /// Переносит работу из потоков HTTP-сервера в UI-поток Revit.
    /// Revit API можно вызывать только оттуда, поэтому запросы ставятся в очередь,
    /// а вызывающий поток блокируется до готовности результата.
    /// </summary>
    public class RevitTaskRunner : IExternalEventHandler
    {
        private class WorkItem
        {
            public Func<UIApplication, object> Work;
            public object Result;
            public Exception Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private readonly ConcurrentQueue<WorkItem> _queue = new ConcurrentQueue<WorkItem>();
        private ExternalEvent _event;

        public void Initialize()
        {
            _event = ExternalEvent.Create(this);
        }

        /// <summary>Выполняет действие в UI-потоке Revit и возвращает его результат.</summary>
        /// <exception cref="TimeoutException">Revit не обработал запрос за отведённое время.</exception>
        public object Run(Func<UIApplication, object> work, TimeSpan timeout)
        {
            if (_event == null)
                throw new InvalidOperationException("RevitTaskRunner не инициализирован.");

            var item = new WorkItem { Work = work };
            _queue.Enqueue(item);
            _event.Raise();

            if (!item.Done.Wait(timeout))
            {
                // Задача останется в очереди и отработает позже — отменить внешнее событие нельзя,
                // поэтому помечаем её как брошенную, чтобы результат просто отбросили.
                Interlocked.Exchange(ref item.Work, null);
                throw new TimeoutException(
                    "Revit не ответил за " + (int)timeout.TotalSeconds + " с. " +
                    "Возможно, открыт модальный диалог или выполняется длительная операция.");
            }

            if (item.Error != null)
                throw item.Error;

            return item.Result;
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var item))
            {
                var work = Interlocked.Exchange(ref item.Work, null);
                if (work == null)
                {
                    // Вызывающая сторона уже отвалилась по таймауту.
                    continue;
                }

                try
                {
                    item.Result = work(app);
                }
                catch (Exception ex)
                {
                    item.Error = ex;
                }
                finally
                {
                    item.Done.Set();
                }
            }
        }

        public string GetName() => "MCP Revit Bridge";
    }
}
