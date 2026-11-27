use std::panic::{catch_unwind, AssertUnwindSafe};

/// Runs `body` and returns its value, or `on_error` when it returns `None` or panics.
///
/// Keeps panics from unwinding across the C boundary into .NET.
pub(crate) fn guard<T>(on_error: T, body: impl FnOnce() -> Option<T>) -> T {
    catch_unwind(AssertUnwindSafe(body))
        .ok()
        .flatten()
        .unwrap_or(on_error)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guard_returns_error_value_on_none() {
        assert_eq!(guard(-1, || None::<i32>), -1);
    }

    #[test]
    fn guard_returns_error_value_on_panic() {
        assert_eq!(guard(-1, || -> Option<i32> { panic!("boom") }), -1);
    }

    #[test]
    fn guard_returns_body_value() {
        assert_eq!(guard(-1, || Some(7)), 7);
    }
}
