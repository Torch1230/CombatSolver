use core::fmt;
use sts2_types::{DecError, RngError};

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum KernelError {
    Unsupported {
        kind: &'static str,
        id: String,
    },
    InvalidAction(String),
    InvalidState(String),
    SnapshotImport {
        field_path: String,
        message: String,
    },
    Dec(DecError),
    Rng(RngError),
    DeathPreventionLoop,
}

impl fmt::Display for KernelError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Unsupported { kind, id } => write!(f, "unsupported {kind}: {id}"),
            Self::InvalidAction(msg) => write!(f, "invalid action: {msg}"),
            Self::InvalidState(msg) => write!(f, "invalid state: {msg}"),
            Self::SnapshotImport {
                field_path,
                message,
            } => write!(f, "snapshot import error at {field_path}: {message}"),
            Self::Dec(err) => write!(f, "decimal error: {err}"),
            Self::Rng(err) => write!(f, "rng error: {err}"),
            Self::DeathPreventionLoop => {
                write!(f, "death prevention repeatedly left the creature dead")
            }
        }
    }
}

impl std::error::Error for KernelError {}

impl From<DecError> for KernelError {
    fn from(value: DecError) -> Self {
        Self::Dec(value)
    }
}

impl From<RngError> for KernelError {
    fn from(value: RngError) -> Self {
        Self::Rng(value)
    }
}
