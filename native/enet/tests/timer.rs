//! Windows timer resolution. Its own test binary, so no other test changes the process timer.

#![cfg(windows)]

mod common;

use std::time::Instant;

use common::{empty_event, server};
use enet::{enet_deinitialize, enet_host_destroy, enet_host_service, enet_initialize};

const CALLS: u32 = 100;
/// Average budget per one-millisecond wait. The default Windows timer gives about 15 ms; with a
/// 1 ms period it is about 1-2 ms. 8 ms keeps the test stable on a loaded machine.
const AVERAGE_BUDGET_MS: f64 = 8.0;

#[test]
fn initialize_gives_one_millisecond_service_waits() {
    assert_eq!(enet_initialize(), 0);
    let (host, _) = server("127.0.0.1");
    let mut event = empty_event();

    let started = Instant::now();
    for _ in 0..CALLS {
        // SAFETY: `host` is live and `event` is valid for writes.
        assert_eq!(unsafe { enet_host_service(host, &mut event, 1) }, 0);
    }
    let average_ms = started.elapsed().as_secs_f64() * 1000.0 / f64::from(CALLS);

    // SAFETY: `host` is live and destroyed once.
    unsafe { enet_host_destroy(host) };
    enet_deinitialize();
    println!("average enet_host_service(host, event, 1): {average_ms:.2} ms");
    assert!(
        average_ms < AVERAGE_BUDGET_MS,
        "average wait {average_ms:.2} ms is not below {AVERAGE_BUDGET_MS} ms"
    );
}
