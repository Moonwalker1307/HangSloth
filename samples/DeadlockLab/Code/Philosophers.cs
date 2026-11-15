namespace DeadlockLab.Code;

public sealed class Fork(int number)
{
    public int Number { get; } = number;
    public override string ToString() => $"fork {Number}";
}

public sealed class Philosopher(string name, Fork left, Fork right)
{
    public void Dine(Barrier everyoneHasALeftFork)
    {
        lock (left)
        {
            everyoneHasALeftFork.SignalAndWait();
            lock (right)
            {
                Console.WriteLine($"{name} eats");
            }
        }
    }
}

internal static class Philosophers
{
    private static readonly Fork[] Forks = [new(0), new(1), new(2)];

    public static void Run()
    {
        using var barrier = new Barrier(Forks.Length);
        string[] names = ["plato", "kant", "hume"];
        for (var i = 0; i < Forks.Length; i++)
        {
            var p = new Philosopher(names[i], Forks[i], Forks[(i + 1) % Forks.Length]);
            new Thread(() => p.Dine(barrier)) { Name = names[i] }.Start();
        }
        Ready.SignalLater();
        Ready.Forever();
    }
}
