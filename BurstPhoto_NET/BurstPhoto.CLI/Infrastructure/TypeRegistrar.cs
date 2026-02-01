using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using Spectre.Console;

namespace BurstPhoto.CLI.Infrastructure;

public class TypeRegistrar(IServiceCollection builder) : ITypeRegistrar
{
    public ITypeResolver Build()
    {
        return new TypeResolver(builder.BuildServiceProvider());
    }

    public void Register(Type service, Type implementation)
    {
        builder.AddSingleton(service, implementation);
    }

    public void RegisterInstance(Type service, object implementation)
    {
        builder.AddSingleton(service, implementation);
    }

    public void RegisterLazy(Type service, Func<object> factory)
    {
        builder.AddSingleton(service, _ => factory());
    }
}

public class TypeResolver(IServiceProvider provider) : ITypeResolver, IDisposable
{
    public object? Resolve(Type? type)
    {
        if (type is null)
        {
            return null;
        }
        try
        {
            return provider.GetService(type);
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            throw;
        }
    }

    public void Dispose()
    {
        if (provider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
