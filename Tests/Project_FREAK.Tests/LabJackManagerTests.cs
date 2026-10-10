using System;
using System.Threading;
using System.Threading.Tasks;
using Project_FREAK.Controllers;
using Xunit;

namespace Project_FREAK.Tests;

public class LabJackManagerTests
{
    [Fact]
    public async Task StartStreaming_InDemoMode_FiresBatchDataUpdatedWithValidPoints()
    {
        var manager = LabJackManager.Instance;
        var tcs = new TaskCompletionSource<DataPointRecord[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(DataPointRecord[] batch)
        {
            tcs.TrySetResult(batch);
        }

        manager.BatchDataUpdated += Handler;
        try
        {
            manager.StartStreaming(1000);
            Assert.True(manager.IsStreaming);

            var batchTask = tcs.Task;
            var completedTask = await Task.WhenAny(batchTask, Task.Delay(2000));
            Assert.Same(batchTask, completedTask);

            var batch = await batchTask;
            Assert.NotEmpty(batch);
            Assert.InRange(batch.Length, 10, 200);

            for (int i = 0; i < batch.Length; i++)
            {
                Assert.True(batch[i].Time >= 0);
                Assert.True(double.IsFinite(batch[i].Thrust));
                Assert.True(double.IsFinite(batch[i].Pressure));
                Assert.True(double.IsFinite(batch[i].ThrustVoltage));
                Assert.True(double.IsFinite(batch[i].PressureVoltage));
            }
        }
        finally
        {
            manager.BatchDataUpdated -= Handler;
            manager.StopStreaming();
            Assert.False(manager.IsStreaming);
        }
    }

    [Fact]
    public async Task ReconnectDevice_WhileStreaming_ResumesIdleReads()
    {
        var manager = LabJackManager.Instance;
        manager.StartStreaming(1000);
        try
        {
            manager.ReconnectDevice();
            Assert.False(manager.IsStreaming);

            var idleReadTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(double thrustVoltage, double thrust, double pressureVoltage, double pressure)
            {
                idleReadTcs.TrySetResult(true);
            }

            manager.DataUpdated += Handler;
            try
            {
                var idleReadTask = idleReadTcs.Task;
                var completedTask = await Task.WhenAny(idleReadTask, Task.Delay(2000));
                Assert.Same(idleReadTask, completedTask);
            }
            finally
            {
                manager.DataUpdated -= Handler;
            }
        }
        finally
        {
            manager.StopStreaming();
        }
    }
}
