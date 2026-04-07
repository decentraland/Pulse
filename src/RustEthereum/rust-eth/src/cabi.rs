use std::ffi::{c_char, CStr};

/// # Safety
///
/// The foreign language must provide valid pointers: expected_signer_address and message as
/// null-terminated C strings, and signature as a pointer to 65 bytes.
#[no_mangle]
pub unsafe extern "C" fn eth_verify_message(
    expected_signer_address: *const c_char,
    message: *const c_char,
    signature: *const u8,
) -> bool {
    let address_str = CStr::from_ptr(expected_signer_address).to_str().unwrap();
    let string_message = CStr::from_ptr(message).to_str().unwrap();
    let sig_bytes: &[u8; 65] = &*(signature as *const [u8; 65]);
    crate::verify::verify_message(address_str, string_message, sig_bytes).unwrap_or(false)
}