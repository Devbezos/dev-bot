using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;

[DisallowConcurrentExecution]
public sealed class SchedulerTickJob : IJob
{
    private readonly BotService _botService;

    public SchedulerTickJob(BotService botService)
    {
        _botService = botService;
    }

    public Task Execute(IJobExecutionContext context) =>
        _botService.RunScheduledTick(context.CancellationToken);
}

public sealed class QuartzSchedulerHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private IScheduler? _scheduler;

    public QuartzSchedulerHostedService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var factory = new StdSchedulerFactory();
        _scheduler = await factory.GetScheduler(cancellationToken);
        _scheduler.JobFactory = new ServiceProviderJobFactory(_serviceProvider);

        var job = JobBuilder.Create<SchedulerTickJob>()
            .WithIdentity("scheduler-tick")
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity("scheduler-tick-every-minute")
            .StartNow()
            .WithSimpleSchedule(x => x
                .WithIntervalInMinutes(1)
                .RepeatForever()
                .WithMisfireHandlingInstructionNextWithExistingCount())
            .Build();

        await _scheduler.ScheduleJob(job, trigger, cancellationToken);
        await _scheduler.Start(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_scheduler != null)
            await _scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken);
    }

    private sealed class ServiceProviderJobFactory : IJobFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public ServiceProviderJobFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler) =>
            _serviceProvider.GetRequiredService(bundle.JobDetail.JobType) as IJob
            ?? throw new InvalidOperationException($"Job type {bundle.JobDetail.JobType.FullName} is not an IJob.");

        public void ReturnJob(IJob job)
        {
        }
    }
}






