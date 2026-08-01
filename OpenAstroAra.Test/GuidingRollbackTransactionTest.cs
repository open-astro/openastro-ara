using NUnit.Framework;
using OpenAstroAra.Server.Services.Guiding;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class GuidingRollbackTransactionTest {
    [Test]
    public async Task ExecuteAll_runs_every_restore_operation_in_order() {
        var calls = new List<int>();

        await GuidingRollbackTransaction.ExecuteAllAsync(new List<Func<Task>> {
            () => { calls.Add(1); return Task.CompletedTask; },
            () => { calls.Add(2); return Task.CompletedTask; },
            () => { calls.Add(3); return Task.CompletedTask; },
        });

        Assert.That(calls, Is.EqualTo(new List<int> { 1, 2, 3 }));
    }

    [Test]
    public void ExecuteAll_continues_after_failure_and_reports_all_errors() {
        var calls = new List<int>();

        var error = Assert.ThrowsAsync<AggregateException>(() =>
            GuidingRollbackTransaction.ExecuteAllAsync(new List<Func<Task>> {
                () => { calls.Add(1); throw new InvalidOperationException("first"); },
                () => { calls.Add(2); return Task.FromException(new TimeoutException("second")); },
                () => { calls.Add(3); return Task.CompletedTask; },
            }));

        Assert.That(calls, Is.EqualTo(new List<int> { 1, 2, 3 }));
        Assert.That(error!.InnerExceptions, Has.Count.EqualTo(2));
        Assert.That(error.InnerExceptions[0].Message, Is.EqualTo("first"));
        Assert.That(error.InnerExceptions[1].Message, Is.EqualTo("second"));
    }

    [Test]
    public void ExecuteAll_rejects_null_operation() {
        Assert.ThrowsAsync<ArgumentNullException>(() =>
            GuidingRollbackTransaction.ExecuteAllAsync(new List<Func<Task>> { null! }));
    }
}
