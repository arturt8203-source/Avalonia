using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Elektrykpomocnik.Services;
using Elektrykpomocnik.ViewModels;
using System;

namespace Elektrykpomocnik.Avalonia;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    /// <summary>Główne okno aplikacji - dostępne dla dialogów</summary>
    public static MainWindow? MainWindowInstance { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();

        // Initialize services

    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();

            // Initialize DialogService with Window handle
            var dialogService = Services.GetRequiredService<IDialogService>();
            dialogService.Initialize(mainWindow);



            MainWindowInstance = mainWindow;
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // ViewModels
        services.AddTransient<MainViewModel>();

        // Views (Window)
        services.AddTransient<MainWindow>();

        // Core Services
        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<IDialogService, DialogService>();
    }
}

public static class DragDropFormats
{
    public static readonly DataFormat<string> ModuleType = DataFormat.CreateStringApplicationFormat("Elektrykpomocnik.ModuleType");
    public static readonly DataFormat<string> ModuleName = DataFormat.CreateStringApplicationFormat("Elektrykpomocnik.ModuleName");
    public static readonly DataFormat<string> ModuleFilePath = DataFormat.CreateStringApplicationFormat("Elektrykpomocnik.ModuleFilePath");
}

