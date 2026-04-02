use alloy_primitives::{Address, Signature};

/// Verify a message signature against an expected signer address.
pub fn verify_message(
    expected_signer_address: &str,
    message: &str,
    signature: &[u8; 65],
) -> Result<bool, std::fmt::Error> {
    let expected: Address = expected_signer_address.parse().map_err(|_| std::fmt::Error)?;
    let sig = Signature::try_from(signature.as_slice()).map_err(|_| std::fmt::Error)?;
    let recovered = sig
        .recover_address_from_msg(message.as_bytes())
        .map_err(|_| std::fmt::Error)?;
    Ok(recovered == expected)
}

#[cfg(test)]
mod tests {
    use alloy_primitives::B256;
    use alloy_signer::SignerSync;
    use alloy_signer_local::PrivateKeySigner;

    use super::*;

    fn test_signer() -> PrivateKeySigner {
        let key_str = "64fdd126fe0e2de2ccbea065d710e9939d083ec96bb9933b750013f30ee81004";
        let fixed_bytes = B256::from_slice(&hex::decode(key_str).unwrap());
        PrivateKeySigner::from_bytes(&fixed_bytes).unwrap()
    }

    #[test]
    fn verify_valid_signature() {
        let signer = test_signer();
        let address = format!("{:?}", signer.address());
        let signature = signer.sign_message_sync("Test message".as_bytes()).unwrap();

        assert!(verify_message(&address, "Test message", &signature.as_bytes()).unwrap());
    }

    #[test]
    fn verify_wrong_message_fails() {
        let signer = test_signer();
        let address = format!("{:?}", signer.address());
        let signature = signer.sign_message_sync("Test message".as_bytes()).unwrap();

        assert!(!verify_message(&address, "Wrong message", &signature.as_bytes()).unwrap());
    }

    #[test]
    fn verify_wrong_address_fails() {
        let signer = test_signer();
        let signature = signer.sign_message_sync("Test message".as_bytes()).unwrap();

        assert!(!verify_message(
            "0x0000000000000000000000000000000000000000",
            "Test message",
            &signature.as_bytes()
        )
        .unwrap());
    }
}