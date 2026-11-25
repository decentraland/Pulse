use std::time::{Duration, Instant};

/// Shortest time between two diagnostic lines from one host.
pub(crate) const LOG_INTERVAL: Duration = Duration::from_secs(5);

/// Rate-limits a host's diagnostic lines to one per [`LOG_INTERVAL`] and counts the lines held
/// back in between.
pub(crate) struct LogLimiter {
    last: Option<Instant>,
    suppressed: u64,
}

impl LogLimiter {
    pub(crate) fn new() -> LogLimiter {
        LogLimiter {
            last: None,
            suppressed: 0,
        }
    }

    /// Returns the number of lines suppressed since the previous one when a line is due at
    /// `now`, and `None` (counting this line as suppressed) otherwise.
    pub(crate) fn admit(&mut self, now: Instant) -> Option<u64> {
        if self
            .last
            .is_some_and(|last| now.saturating_duration_since(last) < LOG_INTERVAL)
        {
            self.suppressed += 1;
            return None;
        }
        self.last = Some(now);
        Some(std::mem::take(&mut self.suppressed))
    }
}

#[cfg(test)]
mod tests {
    use std::time::{Duration, Instant};

    use super::*;

    #[test]
    fn a_line_is_due_at_first_use() {
        let mut limiter = LogLimiter::new();

        assert_eq!(limiter.admit(Instant::now()), Some(0));
    }

    #[test]
    fn a_line_is_not_due_within_the_interval() {
        let start = Instant::now();
        let mut limiter = LogLimiter::new();
        limiter.admit(start);

        assert_eq!(limiter.admit(start), None);
        assert_eq!(
            limiter.admit(start + LOG_INTERVAL - Duration::from_millis(1)),
            None
        );
    }

    #[test]
    fn a_line_is_due_again_after_the_interval_with_the_suppressed_count() {
        let start = Instant::now();
        let mut limiter = LogLimiter::new();
        limiter.admit(start);
        limiter.admit(start + Duration::from_secs(1));
        limiter.admit(start + Duration::from_secs(2));
        limiter.admit(start + Duration::from_secs(3));

        assert_eq!(limiter.admit(start + LOG_INTERVAL), Some(3));
        assert_eq!(limiter.admit(start + LOG_INTERVAL), None);
        assert_eq!(limiter.admit(start + LOG_INTERVAL * 2), Some(1));
    }
}
