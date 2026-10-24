using Microsoft.Data.Sqlite;
using WechatDashboard.App.ViewModels.Messages;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Todos;
using WechatDashboard.Domain.Enums;
using WechatDashboard.Infrastructure.Persistence;

var db = @"D:\study\code\wechatDashboard\tools\result\data\wechat-dashboard.db";
var ids = new List<long>();
using (var connection = new SqliteConnection($"Data Source={db}"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT DISTINCT source_message_id FROM todo_items WHERE source_message_id IS NOT NULL ORDER BY source_message_id DESC LIMIT 5;";
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
        ids.Add(reader.GetInt64(0));
    }
}

Console.WriteLine($"candidate message ids: {string.Join(", ", ids)}");
if (ids.Count == 0)
{
    return;
}

var messages = new SqliteMessageRepository(db);
var todoRepository = new SqliteTodoRepository(db);
var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(db), SystemClock.Instance);

static MessageFeedViewModel CreateFeed(
    SqliteMessageRepository messages,
    SqliteTodoRepository todoRepository,
    TodoApplicationService todoService) =>
    new(
        messages,
        todoRepository,
        todoService,
        () => Array.Empty<string>(),
        () => Array.Empty<string>(),
        () => FollowedChatFilterMode.Include,
        _ => Task.CompletedTask);

var target = ids[0];

// 场景一：单独跳转上下文
try
{
    var feed = CreateFeed(messages, todoRepository, todoService);
    await feed.ShowContextAsync(target);
    Console.WriteLine($"[单独跳转] OK status={feed.StatusText} items={feed.Items.Count} selected={feed.SelectedMessage?.MessageId}");
}
catch (Exception ex)
{
    Console.WriteLine($"[单独跳转] 异常 {ex.GetType().Name}: {ex.Message}");
}

// 场景三：验证“旧滚动逻辑”确实会因选中项被清空而崩溃（说明新增回归测试有意义）
var staThread = new Thread(() =>
{
    try
    {
        var grid = new System.Windows.Controls.DataGrid
        {
            ItemsSource = new System.Collections.ObjectModel.ObservableCollection<string> { "第一条", "第二条" }
        };
        grid.SelectedItem = "第一条";
        Console.WriteLine($"[旧滚动逻辑] 选中生效={grid.SelectedItem is not null}");
        grid.SelectedItem = null;
        grid.Dispatcher.BeginInvoke(new Action(() => grid.ScrollIntoView(grid.SelectedItem)));
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => { }));
        Console.WriteLine("[旧滚动逻辑] 未抛异常");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[旧滚动逻辑] 异常 {ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
    }
});
staThread.SetApartmentState(ApartmentState.STA);
staThread.Start();
staThread.Join();
for (var attempt = 0; attempt < 5; attempt++)
{
    try
    {
        var feed = CreateFeed(messages, todoRepository, todoService);
        feed.PrepareContextNavigation(target);
        var refresh = feed.RefreshAsync();
        var context = feed.ShowContextAsync(target);
        await Task.WhenAll(refresh, context);
        Console.WriteLine($"[并发 #{attempt}] OK status={feed.StatusText} items={feed.Items.Count} selected={feed.SelectedMessage?.MessageId}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[并发 #{attempt}] 异常 {ex.GetType().Name}: {ex.Message}");
    }
}
