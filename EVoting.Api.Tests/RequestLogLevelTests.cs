using EVoting.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Serilog.Events;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>The level of Serilog's "request finished" line: a caller that went away is not an ERROR, everything else is as it was.</summary>
    public class RequestLogLevelTests
    {
        private static DefaultHttpContext Request(bool callerGone, int status = 200)
        {
            var cts = new CancellationTokenSource();
            if (callerGone) cts.Cancel();
            var context = new DefaultHttpContext { RequestAborted = cts.Token };
            context.Response.StatusCode = status;
            return context;
        }

        [Fact]
        public void A_caller_that_closed_the_connection_is_not_an_error()
        {
            Assert.Equal(LogEventLevel.Debug, RequestLogLevel.For(Request(callerGone: true), 12, new OperationCanceledException()));
        }

        [Fact]
        public void A_cancelled_task_of_a_caller_that_closed_the_connection_is_not_an_error()
        {
            // TaskCanceledException derives from OperationCanceledException: it is what an awaited database call throws
            Assert.Equal(LogEventLevel.Debug, RequestLogLevel.For(Request(callerGone: true), 12, new TaskCanceledException()));
        }

        [Fact]
        public void A_cancellation_nobody_asked_for_stays_an_error()
        {
            // the caller is still waiting (for instance a server-side timeout): that is a failure
            Assert.Equal(LogEventLevel.Error, RequestLogLevel.For(Request(callerGone: false), 12, new OperationCanceledException()));
        }

        [Fact]
        public void Any_other_exception_is_an_error_even_when_the_caller_has_gone()
        {
            Assert.Equal(LogEventLevel.Error, RequestLogLevel.For(Request(callerGone: true), 12, new InvalidOperationException("boom")));
            Assert.Equal(LogEventLevel.Error, RequestLogLevel.For(Request(callerGone: false), 12, new InvalidOperationException("boom")));
        }

        [Theory]
        [InlineData(200, false)]
        [InlineData(204, false)]
        [InlineData(404, false)]
        [InlineData(409, false)]
        [InlineData(499, false)]
        [InlineData(500, true)]
        [InlineData(503, true)]
        public void Without_an_exception_only_a_5xx_answer_is_an_error(int status, bool isError)
        {
            Assert.Equal(isError ? LogEventLevel.Error : LogEventLevel.Information, RequestLogLevel.For(Request(callerGone: false, status), 12, null));
        }

        [Fact]
        public void A_5xx_answer_is_an_error_even_when_the_caller_has_gone()
        {
            // no exception here, so the "abandoned" rule must not hide it
            Assert.Equal(LogEventLevel.Error, RequestLogLevel.For(Request(callerGone: true, status: 500), 12, null));
        }
    }
}
