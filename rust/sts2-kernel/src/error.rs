use core::fmt;
use sts2_types::{DecError, RngError};

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum KernelErrorKind {
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
    PotionPolicyUnsatisfied(String),
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct KernelError(Box<KernelErrorKind>);

const _: () = assert!(std::mem::size_of::<KernelError>() == 8);
const _: () = assert!(std::mem::size_of::<Result<(), KernelError>>() == 8);
const _: () = assert!(std::mem::size_of::<Result<bool, KernelError>>() == 16);
const _: () = assert!(std::mem::size_of::<Result<i32, KernelError>>() == 16);
const _: () = assert!(std::mem::size_of::<Result<sts2_types::Dec, KernelError>>() == 24);

impl KernelError {
    #[inline(always)]
    pub fn kind(&self) -> &KernelErrorKind {
        &self.0
    }

    #[inline(always)]
    pub fn into_kind(self) -> KernelErrorKind {
        *self.0
    }

    #[allow(non_snake_case)]
    #[cold]
    #[inline(never)]
    pub fn InvalidState(msg: String) -> Self {
        Self(Box::new(KernelErrorKind::InvalidState(msg)))
    }

    #[allow(non_snake_case)]
    #[cold]
    #[inline(never)]
    pub fn InvalidAction(msg: String) -> Self {
        Self(Box::new(KernelErrorKind::InvalidAction(msg)))
    }

    #[allow(non_snake_case)]
    #[cold]
    #[inline(never)]
    pub fn Dec(err: DecError) -> Self {
        Self(Box::new(KernelErrorKind::Dec(err)))
    }

    #[allow(non_snake_case)]
    #[cold]
    #[inline(never)]
    pub fn Rng(err: RngError) -> Self {
        Self(Box::new(KernelErrorKind::Rng(err)))
    }

    #[cold]
    #[inline(never)]
    pub fn unsupported(kind: &'static str, id: String) -> Self {
        Self(Box::new(KernelErrorKind::Unsupported { kind, id }))
    }

    #[cold]
    #[inline(never)]
    pub fn snapshot_import(field_path: String, message: String) -> Self {
        Self(Box::new(KernelErrorKind::SnapshotImport {
            field_path,
            message,
        }))
    }

    #[cold]
    #[inline(never)]
    pub fn death_prevention_loop() -> Self {
        Self(Box::new(KernelErrorKind::DeathPreventionLoop))
    }

    #[cold]
    #[inline(never)]
    pub fn potion_policy_unsatisfied(msg: String) -> Self {
        Self(Box::new(KernelErrorKind::PotionPolicyUnsatisfied(msg)))
    }
}

impl fmt::Display for KernelError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self.0.as_ref() {
            KernelErrorKind::Unsupported { kind, id } => write!(f, "unsupported {kind}: {id}"),
            KernelErrorKind::InvalidAction(msg) => write!(f, "invalid action: {msg}"),
            KernelErrorKind::InvalidState(msg) => write!(f, "invalid state: {msg}"),
            KernelErrorKind::SnapshotImport {
                field_path,
                message,
            } => write!(f, "snapshot import error at {field_path}: {message}"),
            KernelErrorKind::Dec(err) => write!(f, "decimal error: {err}"),
            KernelErrorKind::Rng(err) => write!(f, "rng error: {err}"),
            KernelErrorKind::DeathPreventionLoop => {
                write!(f, "death prevention repeatedly left the creature dead")
            }
            KernelErrorKind::PotionPolicyUnsatisfied(msg) => {
                write!(f, "hard potion policy unsatisfied: {msg}")
            }
        }
    }
}

impl std::error::Error for KernelError {}

impl From<DecError> for KernelError {
    #[cold]
    #[inline(never)]
    fn from(value: DecError) -> Self {
        Self(Box::new(KernelErrorKind::Dec(value)))
    }
}

impl From<RngError> for KernelError {
    #[cold]
    #[inline(never)]
    fn from(value: RngError) -> Self {
        Self(Box::new(KernelErrorKind::Rng(value)))
    }
}
