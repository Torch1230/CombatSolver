pub mod act1;
pub mod act2;
pub mod act3_shared;

use smallvec::SmallVec;
use sts2_kernel::{
    BattleState, CardHandle, ContentDispatcher, CreatureHandle, HookArgs, HookResult, KernelError,
};
use sts2_types::{Dec, HookSlot, MonsterId, MonsterMeta};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum MonsterPartition {
    Act1,
    Act2,
    Act3Shared,
}

const fn monster_partition(id: MonsterId) -> MonsterPartition {
    match id {
        MonsterId::AssassinRubyRaider
        | MonsterId::AxeRubyRaider
        | MonsterId::BruteRubyRaider
        | MonsterId::BygoneEffigy
        | MonsterId::Byrdonis
        | MonsterId::CalcifiedCultist
        | MonsterId::CeremonialBeast
        | MonsterId::CorpseSlug
        | MonsterId::CrossbowRubyRaider
        | MonsterId::CubexConstruct
        | MonsterId::DampCultist
        | MonsterId::Flyconid
        | MonsterId::Fogmog
        | MonsterId::FossilStalker
        | MonsterId::FuzzyWurmCrawler
        | MonsterId::GremlinMerc
        | MonsterId::HauntedShip
        | MonsterId::Inklet
        | MonsterId::KinFollower
        | MonsterId::KinPriest
        | MonsterId::LagavulinMatriarch
        | MonsterId::LeafSlimeM
        | MonsterId::LeafSlimeS
        | MonsterId::LivingFog
        | MonsterId::Mawler
        | MonsterId::Nibbit
        | MonsterId::PhantasmalGardener
        | MonsterId::PhrogParasite
        | MonsterId::PunchConstruct
        | MonsterId::Seapunk
        | MonsterId::SewerClam
        | MonsterId::ShrinkerBeetle
        | MonsterId::SkulkingColony
        | MonsterId::SlitheringStrangler
        | MonsterId::SludgeSpinner
        | MonsterId::SnappingJaxfruit
        | MonsterId::SoulFysh
        | MonsterId::TerrorEel
        | MonsterId::Toadpole
        | MonsterId::TrackerRubyRaider
        | MonsterId::TwigSlimeM
        | MonsterId::TwigSlimeS
        | MonsterId::TwoTailedRat
        | MonsterId::Vantom
        | MonsterId::VineShambler
        | MonsterId::WaterfallGiant => MonsterPartition::Act1,

        MonsterId::BowlbugEgg
        | MonsterId::BowlbugNectar
        | MonsterId::BowlbugRock
        | MonsterId::BowlbugSilk
        | MonsterId::Chomper
        | MonsterId::Crusher
        | MonsterId::DecimillipedeSegmentBack
        | MonsterId::DecimillipedeSegmentFront
        | MonsterId::DecimillipedeSegmentMiddle
        | MonsterId::Entomancer
        | MonsterId::Exoskeleton
        | MonsterId::HunterKiller
        | MonsterId::InfestedPrism
        | MonsterId::KnowledgeDemon
        | MonsterId::LouseProgenitor
        | MonsterId::Myte
        | MonsterId::Ovicopter
        | MonsterId::Rocket
        | MonsterId::SlumberingBeetle
        | MonsterId::SpinyToad
        | MonsterId::TheInsatiable
        | MonsterId::TheObscura
        | MonsterId::ThievingHopper
        | MonsterId::Tunneler => MonsterPartition::Act2,

        MonsterId::Aeonglass
        | MonsterId::Axebot
        | MonsterId::BattleFriendV1
        | MonsterId::BattleFriendV2
        | MonsterId::BattleFriendV3
        | MonsterId::ByrdineMonster
        | MonsterId::Byrdpip
        | MonsterId::DevotedSculptor
        | MonsterId::EyeWithTeeth
        | MonsterId::Fabricator
        | MonsterId::FakeMerchantMonster
        | MonsterId::FatGremlin
        | MonsterId::FlailKnight
        | MonsterId::FrogKnight
        | MonsterId::GasBomb
        | MonsterId::GlobeHead
        | MonsterId::Guardbot
        | MonsterId::LivingShield
        | MonsterId::MagiKnight
        | MonsterId::MechaKnight
        | MonsterId::MysteriousKnight
        | MonsterId::Noisebot
        | MonsterId::Osty
        | MonsterId::OwlMagistrate
        | MonsterId::PaelsLegion
        | MonsterId::Parafright
        | MonsterId::Queen
        | MonsterId::ScrollOfBiting
        | MonsterId::SlimedBerserker
        | MonsterId::SneakyGremlin
        | MonsterId::SoulNexus
        | MonsterId::SpectralKnight
        | MonsterId::Stabbot
        | MonsterId::TestSubject
        | MonsterId::TheForgotten
        | MonsterId::TheLost
        | MonsterId::TorchHeadAmalgam
        | MonsterId::ToughEgg
        | MonsterId::TurretOperator
        | MonsterId::Wriggler
        | MonsterId::Wuppy
        | MonsterId::Zapbot => MonsterPartition::Act3Shared,
    }
}

pub fn monster_meta(id: MonsterId) -> Result<&'static MonsterMeta, KernelError> {
    match monster_partition(id) {
        MonsterPartition::Act1 => act1::monster_meta(id),
        MonsterPartition::Act2 => act2::monster_meta(id),
        MonsterPartition::Act3Shared => act3_shared::monster_meta(id),
    }
}

pub fn monster_initial_hp_range(
    state: &BattleState,
    monster_id: MonsterId,
) -> Result<(i32, i32), KernelError> {
    match monster_partition(monster_id) {
        MonsterPartition::Act1 => act1::monster_initial_hp_range(state, monster_id),
        MonsterPartition::Act2 => act2::monster_initial_hp_range(state, monster_id),
        MonsterPartition::Act3Shared => act3_shared::monster_initial_hp_range(state, monster_id),
    }
}

pub fn monster_setup_for_combat(
    state: &mut BattleState,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let mid = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    match monster_partition(mid) {
        MonsterPartition::Act1 => act1::monster_setup_for_combat(state, creature),
        MonsterPartition::Act2 => act2::monster_setup_for_combat(state, creature),
        MonsterPartition::Act3Shared => act3_shared::monster_setup_for_combat(state, creature),
    }
}

pub fn monster_after_added_to_room(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let mid = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    match monster_partition(mid) {
        MonsterPartition::Act1 => act1::monster_after_added_to_room(content, state, creature),
        MonsterPartition::Act2 => act2::monster_after_added_to_room(content, state, creature),
        MonsterPartition::Act3Shared => {
            act3_shared::monster_after_added_to_room(content, state, creature)
        }
    }
}

pub fn monster_roll_move(
    state: &mut BattleState,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let mid = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    match monster_partition(mid) {
        MonsterPartition::Act1 => act1::monster_roll_move(state, creature),
        MonsterPartition::Act2 => act2::monster_roll_move(state, creature),
        MonsterPartition::Act3Shared => act3_shared::monster_roll_move(state, creature),
    }
}

pub fn monster_perform_move(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
) -> Result<(), KernelError> {
    let mid = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    match monster_partition(mid) {
        MonsterPartition::Act1 => act1::monster_perform_move(content, state, creature),
        MonsterPartition::Act2 => act2::monster_perform_move(content, state, creature),
        MonsterPartition::Act3Shared => act3_shared::monster_perform_move(content, state, creature),
    }
}

pub fn monster_resume_move_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    selected: &[CardHandle],
) -> Result<(), KernelError> {
    let mid = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    match monster_partition(mid) {
        MonsterPartition::Act2 => act2::monster_resume_move_choice(content, state, creature, selected),
        _ => Err(KernelError::Unsupported {
            kind: "MonsterMoveChoiceResume",
            id: mid.model_id_str().to_owned(),
        }),
    }
}

pub fn monster_attack_intent_hits(
    state: &BattleState,
    creature: CreatureHandle,
) -> Result<SmallVec<[(Dec, i32); 2]>, KernelError> {
    let Some(c) = state.creature(creature) else {
        return Ok(SmallVec::new());
    };
    if c.monster
        .as_ref()
        .is_some_and(|m| m.next_move_id.as_deref() == Some("STUNNED"))
    {
        return Ok(SmallVec::new());
    }
    let mid = c
        .monster_id
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    match monster_partition(mid) {
        MonsterPartition::Act1 => act1::monster_attack_intent_hits(state, creature),
        MonsterPartition::Act2 => act2::monster_attack_intent_hits(state, creature),
        MonsterPartition::Act3Shared => act3_shared::monster_attack_intent_hits(state, creature),
    }
}

pub fn dispatch_hook(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    slot: HookSlot,
    args: &HookArgs<'_>,
) -> Result<HookResult, KernelError> {
    let mid = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    match monster_partition(mid) {
        MonsterPartition::Act1 => act1::dispatch_hook(content, state, creature, slot, args),
        MonsterPartition::Act2 => act2::dispatch_hook(content, state, creature, slot, args),
        MonsterPartition::Act3Shared => {
            act3_shared::dispatch_hook(content, state, creature, slot, args)
        }
    }
}

pub fn resume_hook_choice(
    content: &dyn ContentDispatcher,
    state: &mut BattleState,
    creature: CreatureHandle,
    slot: HookSlot,
    selected: &[CardHandle],
) -> Result<HookResult, KernelError> {
    let mid = state
        .creature(creature)
        .and_then(|c| c.monster_id)
        .ok_or_else(|| KernelError::InvalidState("missing monster".to_owned()))?;
    match monster_partition(mid) {
        MonsterPartition::Act1 => act1::resume_hook_choice(content, state, creature, slot, selected),
        MonsterPartition::Act2 => act2::resume_hook_choice(content, state, creature, slot, selected),
        MonsterPartition::Act3Shared => {
            act3_shared::resume_hook_choice(content, state, creature, slot, selected)
        }
    }
}
