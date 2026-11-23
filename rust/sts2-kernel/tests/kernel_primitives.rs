use smallvec::SmallVec;
use std::collections::BTreeMap;
use std::sync::Arc;
use sts2_kernel::commands::{autoplay, card, card_factory, card_pile, creature, damage, forge, orb, potion, power};
use sts2_kernel::content_dispatch::{CardPlayContext, CardPlayOutcome};
use sts2_kernel::snapshot::{export_live_state, import_live_state};
use sts2_kernel::{
    BattleState, CardExtension, CardHandle, CardInst, CardTempFlags, ChoiceRequest, ChoiceTiming,
    CombatEngine, ContentDispatcher, CreatureHandle, CreatureInst, EnchantmentInst, FieldValue,
    HookArgs, HookListener, HookResult, KernelError, ModifyDamageHookType, MonsterInst,
    PlayerCounters, PlayerInst, RootStaticContext, SemanticHistory, StepOutcome,
};
use sts2_types::{
    AfflictionId, CardId, CardKeywords, CardMeta, CardPilePosition, CardRarity, CardStaticFlags,
    CardTags, CardType, CharacterId, CombatSide, Dec, DominionId, EnchantmentId,
    EnchantmentStatus, GameMode, HookMask, HookSlot, MonsterId, MonsterMeta, OrbId, OrbMeta,
    PileType, PlayerRngSet, PlayerTurnPhase, PotionId, PotionMeta, PowerId, PowerInstanceType,
    PowerMeta, PowerStackType, PowerStaticFlags, PowerType, RelicId, RelicMeta, Rng, RoomType,
    RunRngSet, RunRngType, TargetType, ValueProp,
};

static TEST_STRIKE_META: CardMeta = CardMeta {
    card_type: CardType::Attack,
    rarity: CardRarity::Basic,
    target_type: TargetType::AnyEnemy,
    canonical_energy_cost: 1,
    canonical_star_cost: -1,
    max_upgrade_level: 1,
    base_keywords: CardKeywords::empty(),
    base_tags: CardTags::STRIKE,
    flags: CardStaticFlags::empty(),
    hook_mask: HookMask::EMPTY,
};

static TEST_SOVEREIGN_META: CardMeta = CardMeta {
    card_type: CardType::Attack,
    rarity: CardRarity::Token,
    target_type: TargetType::AnyEnemy,
    canonical_energy_cost: 2,
    canonical_star_cost: -1,
    max_upgrade_level: 1,
    base_keywords: CardKeywords::RETAIN,
    base_tags: CardTags::empty(),
    flags: CardStaticFlags::empty(),
    hook_mask: HookMask::EMPTY,
};

static TEST_POTION_META: PotionMeta = PotionMeta {
    target_type: TargetType::SelfTarget,
    uses_rng: false,
    may_heal: false,
    hook_mask: HookMask::EMPTY,
};

static TEST_MONSTER_META: MonsterMeta = MonsterMeta {
    hook_mask: HookMask::EMPTY,
};

static TEST_ORB_META: OrbMeta = OrbMeta {
    orb_id: OrbId::LightningOrb,
    hook_mask: HookMask::EMPTY,
};

static TEST_POWER_META: PowerMeta = PowerMeta {
    power_type: PowerType::Buff,
    stack_type: PowerStackType::Counter,
    instance_type: PowerInstanceType::None,
    flags: PowerStaticFlags::empty(),
    hook_mask: HookMask::EMPTY,
};

static TEST_RELIC_META: RelicMeta = RelicMeta {
    flags: sts2_types::RelicStaticFlags::empty(),
    hook_mask: HookMask::EMPTY,
};

#[derive(Default)]
struct TestKernelContent {
    pub entered_combat_cards: std::cell::RefCell<Vec<CardId>>,
    pub generated_cards: std::cell::RefCell<Vec<CardId>>,
    pub enchantment_plays: std::cell::RefCell<Vec<EnchantmentId>>,
    pub forge_amounts: std::cell::RefCell<Vec<Dec>>,
    pub potion_events: std::cell::RefCell<Vec<&'static str>>,
    pub dominion_choices: std::cell::RefCell<Vec<usize>>,
}

impl ContentDispatcher for TestKernelContent {
    fn card_meta(&self, id: CardId) -> Result<&'static CardMeta, KernelError> {
        match id {
            CardId::SovereignBlade => Ok(&TEST_SOVEREIGN_META),
            _ => Ok(&TEST_STRIKE_META),
        }
    }

    fn power_meta(&self, _id: PowerId) -> Result<&'static PowerMeta, KernelError> {
        Ok(&TEST_POWER_META)
    }

    fn relic_meta(&self, _id: RelicId) -> Result<&'static RelicMeta, KernelError> {
        Ok(&TEST_RELIC_META)
    }

    fn potion_meta(&self, _id: PotionId) -> Result<&'static PotionMeta, KernelError> {
        Ok(&TEST_POTION_META)
    }

    fn monster_meta(&self, _id: MonsterId) -> Result<&'static MonsterMeta, KernelError> {
        Ok(&TEST_MONSTER_META)
    }

    fn orb_meta(&self, _id: OrbId) -> Result<&'static OrbMeta, KernelError> {
        Ok(&TEST_ORB_META)
    }

    fn card_is_playable(&self, _state: &BattleState, _card: CardHandle) -> Result<bool, KernelError> {
        Ok(true)
    }

    fn card_on_play(
        &self,
        state: &mut BattleState,
        ctx: &CardPlayContext,
        _choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
        _resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        if let Some(target) = ctx.target {
            let owner = state.player.creature_handle;
            sts2_kernel::commands::damage::AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(6),
                sts2_kernel::commands::damage::AttackTargetSpec::Single(target),
            )
            .execute(state, self)?;
        }
        Ok(CardPlayOutcome::default())
    }

    fn card_on_play_resume_after_select(
        &self,
        _state: &mut BattleState,
        _ctx: &CardPlayContext,
        _selected: &[CardHandle],
        _resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        Ok(CardPlayOutcome::default())
    }

    fn card_on_upgrade(&self, _state: &mut BattleState, _card: CardHandle) -> Result<(), KernelError> {
        Ok(())
    }

    fn card_comparison_base_values(
        &self,
        _state: &BattleState,
        _card: CardHandle,
    ) -> Result<SmallVec<[(&'static str, Dec); 4]>, KernelError> {
        Ok(SmallVec::new())
    }

    fn monster_initial_hp_range(
        &self,
        _state: &BattleState,
        _monster_id: MonsterId,
    ) -> Result<(i32, i32), KernelError> {
        Ok((40, 40))
    }

    fn monster_setup_for_combat(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        if let Some(c) = state.creature_mut(creature) {
            if let Some(m) = &mut c.monster {
                m.current_state_id = "IDLE".to_owned();
                m.next_move_id = Some("IDLE".to_owned());
            }
        }
        Ok(())
    }

    fn monster_after_added_to_room(
        &self,
        _state: &mut BattleState,
        _creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        Ok(())
    }

    fn monster_roll_move(
        &self,
        _state: &mut BattleState,
        _creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        Ok(())
    }

    fn monster_perform_move(
        &self,
        _state: &mut BattleState,
        _creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        Ok(())
    }

    fn orb_passive(
        &self,
        _state: &mut BattleState,
        _orb_idx: usize,
        _target: Option<CreatureHandle>,
    ) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
        Ok(SmallVec::new())
    }

    fn orb_evoke(
        &self,
        _state: &mut BattleState,
        _orb_idx: usize,
    ) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
        Ok(SmallVec::new())
    }

    fn orb_before_turn_end(&self, _state: &mut BattleState, _orb_idx: usize) -> Result<(), KernelError> {
        Ok(())
    }

    fn orb_after_turn_start(&self, _state: &mut BattleState, _orb_idx: usize) -> Result<(), KernelError> {
        Ok(())
    }

    fn potion_on_use(
        &self,
        state: &mut BattleState,
        potion_id: PotionId,
        _target: Option<CreatureHandle>,
        choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
        _resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        self.potion_events.borrow_mut().push("on_use");
        if potion_id == PotionId::LiquidMemories && state.player.discard.len() > 1 {
            if choice_queue.is_empty() {
                return Ok(CardPlayOutcome {
                    needs_choice: Some((
                        ChoiceRequest {
                            timing: ChoiceTiming::Action,
                            source_id: potion_id.model_id_str().to_owned(),
                            source_pile: PileType::Discard,
                            min_count: 1,
                            max_count: 1,
                            candidates: state.player.discard.iter().copied().collect(),
                            can_skip: false,
                        },
                        1,
                    )),
                    transform_into: None,
                });
            }
            let sel = choice_queue.remove(0);
            let h = state.player.discard[sel[0]];
            card_pile::add_sync(state, h, PileType::Hand, CardPilePosition::Bottom);
        } else if potion_id == PotionId::FairyInABottle {
            sts2_kernel::commands::creature::heal(
                state,
                self,
                state.player.creature_handle,
                Dec::from_i32(10),
            )?;
        }
        Ok(CardPlayOutcome::default())
    }

    fn potion_on_use_resume_after_select(
        &self,
        state: &mut BattleState,
        _potion_id: PotionId,
        _target: Option<CreatureHandle>,
        selected: &[CardHandle],
        _resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        self.potion_events.borrow_mut().push("on_use_resume");
        for &h in selected {
            card_pile::add_sync(state, h, PileType::Hand, CardPilePosition::Bottom);
        }
        Ok(CardPlayOutcome::default())
    }

    fn resume_hook_choice(
        &self,
        state: &mut BattleState,
        _listener: HookListener,
        _slot: HookSlot,
        selected: &[CardHandle],
    ) -> Result<HookResult, KernelError> {
        self.dominion_choices.borrow_mut().push(selected.len());
        for &h in selected {
            card_pile::add_sync(state, h, PileType::Discard, CardPilePosition::Bottom);
        }
        Ok(HookResult::default())
    }

    fn enchantment_on_play(
        &self,
        _state: &mut BattleState,
        card: CardHandle,
        _ctx: &CardPlayContext,
    ) -> Result<(), KernelError> {
        if let Some(id) = _state
            .card(card)
            .and_then(|c| _state.card_ext(c.ext_index))
            .and_then(|e| e.enchantment.as_ref().map(|x| x.id))
        {
            self.enchantment_plays.borrow_mut().push(id);
        }
        Ok(())
    }

    fn affliction_on_play(
        &self,
        _state: &mut BattleState,
        _card: CardHandle,
        _ctx: &CardPlayContext,
    ) -> Result<(), KernelError> {
        Ok(())
    }

    fn enchantment_play_count(
        &self,
        state: &BattleState,
        card: CardHandle,
        play_count: i32,
    ) -> Result<i32, KernelError> {
        let is_glam = state
            .card(card)
            .and_then(|c| state.card_ext(c.ext_index))
            .and_then(|e| e.enchantment.as_ref())
            .is_some_and(|e| e.id == EnchantmentId::Glam);
        Ok(if is_glam { play_count + 1 } else { play_count })
    }

    fn enchantment_damage_additive(
        &self,
        state: &BattleState,
        card: CardHandle,
        _amount: Dec,
        props: ValueProp,
    ) -> Result<Dec, KernelError> {
        let is_sharp = state
            .card(card)
            .and_then(|c| state.card_ext(c.ext_index))
            .and_then(|e| e.enchantment.as_ref())
            .is_some_and(|e| e.id == EnchantmentId::Sharp);
        Ok(if is_sharp && props.is_powered_attack() {
            Dec::TWO
        } else {
            Dec::ZERO
        })
    }

    fn enchantment_damage_multiplicative(
        &self,
        _state: &BattleState,
        _card: CardHandle,
        _amount: Dec,
        _props: ValueProp,
    ) -> Result<Dec, KernelError> {
        Ok(Dec::ONE)
    }

    fn enchantment_should_start_at_bottom(&self, id: EnchantmentId) -> Result<bool, KernelError> {
        Ok(id == EnchantmentId::Imbued)
    }

    fn enchantment_modify_shuffle_order(
        &self,
        state: &BattleState,
        card: CardHandle,
        cards: &mut Vec<CardHandle>,
        is_initial_shuffle: bool,
    ) -> Result<(), KernelError> {
        let is_perfect_fit = state
            .card(card)
            .and_then(|c| state.card_ext(c.ext_index))
            .and_then(|e| e.enchantment.as_ref())
            .is_some_and(|e| e.id == EnchantmentId::PerfectFit);
        if is_perfect_fit && !is_initial_shuffle {
            cards.retain(|&h| h != card);
            cards.insert(0, card);
        }
        Ok(())
    }

    fn listener_hook_mask(
        &self,
        _state: &BattleState,
        listener: HookListener,
    ) -> Result<HookMask, KernelError> {
        match listener {
            HookListener::Dominion(DominionId::KnowledgeDemonDominion) => {
                Ok(HookMask::EMPTY.with(HookSlot::BeforeHandDraw))
            }
            HookListener::Relic(_) => Ok(HookMask::EMPTY
                .with(HookSlot::AfterCardEnteredCombat)
                .with(HookSlot::AfterCardGenerated)
                .with(HookSlot::AfterForge)
                .with(HookSlot::AfterPotionUsed)
                .with(HookSlot::AfterPotionProcured)),
            _ => Ok(HookMask::EMPTY),
        }
    }

    fn dispatch_hook(
        &self,
        state: &mut BattleState,
        listener: HookListener,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<HookResult, KernelError> {
        match (listener, slot) {
            (HookListener::Dominion(DominionId::KnowledgeDemonDominion), HookSlot::BeforeHandDraw) => {
                let candidates: SmallVec<[CardHandle; 16]> = state.player.draw.iter().take(2).copied().collect();
                Ok(HookResult {
                    needs_choice: Some(ChoiceRequest {
                        timing: ChoiceTiming::PlayerTurnStart,
                        source_id: "DOMINION.KNOWLEDGE_DEMON_DOMINION".to_owned(),
                        source_pile: PileType::Draw,
                        min_count: 1,
                        max_count: 1,
                        candidates,
                        can_skip: false,
                    }),
                    ..Default::default()
                })
            }
            (_, HookSlot::AfterCardEnteredCombat) => {
                if let HookArgs::CardOnly(ch) = args {
                    self.entered_combat_cards
                        .borrow_mut()
                        .push(state.card(*ch).unwrap().id);
                }
                Ok(HookResult::default())
            }
            (_, HookSlot::AfterCardGenerated) => {
                if let HookArgs::CardGenerated { card, .. } = args {
                    self.generated_cards
                        .borrow_mut()
                        .push(state.card(*card).unwrap().id);
                }
                Ok(HookResult::default())
            }
            (_, HookSlot::AfterForge) => {
                if let HookArgs::Forge { amount } = args {
                    self.forge_amounts.borrow_mut().push(*amount);
                }
                Ok(HookResult::default())
            }
            (_, HookSlot::AfterPotionUsed) => {
                self.potion_events.borrow_mut().push("after_used");
                Ok(HookResult::default())
            }
            (_, HookSlot::AfterPotionProcured) => {
                self.potion_events.borrow_mut().push("after_procured");
                Ok(HookResult::default())
            }
            _ => Ok(HookResult::default()),
        }
    }
}

fn make_test_battle(dominion: Option<DominionId>, deck_count: usize) -> (BattleState, CreatureHandle) {
    let run_rngs = RunRngSet::from_string_seed("KERNELTEST");
    let player_rngs = PlayerRngSet::from_seed(run_rngs.seed);
    let pc = CreatureHandle(1);
    let enemy = CreatureHandle(2);
    let mut cards = Vec::new();
    let mut deck = SmallVec::new();
    for i in 0..deck_count {
        let h = CardHandle((i + 1) as u32);
        deck.push(h);
        cards.push(CardInst {
            handle: h,
            id: CardId::StrikeIronclad,
            upgrade_level: 0,
            canonical_base_energy_cost: 1,
            base_energy_cost: 1,
            captured_energy_x_value: 0,
            base_replay_count: 0,
            floor_added_to_deck: Some(1),
            deck_card_handle: Some(h),
            clone_of_handle: None,
            temp_star_cost_override_this_turn: None,
            temp_cost_override_this_turn: None,
            temp_cost_override_this_turn_or_until_played: None,
            temp_cost_override_this_combat: None,
            local_keywords: CardKeywords::empty(),
            keywords_added_from_outside: CardKeywords::empty(),
            keywords_removed_from_outside: CardKeywords::empty(),
            temp_flags: CardTempFlags::empty(),
            affliction: None,
            ext_index: None,
            pile: PileType::Deck,
            removed_from_state: false,
            transform_into_after_play: None,
        });
    }

    let state = BattleState {
        static_ctx: Arc::new(RootStaticContext {
            encounter_id: "FUZZY_WURM_CRAWLER_WEAK".to_owned(),
            encounter_is_weak: true,
            room_type: RoomType::Monster,
            encounter_slots: vec!["slot_0".to_owned()],
            game_mode: GameMode::Standard,
            ascension: 0,
            character_id: CharacterId::Ironclad,
            dominion_id: dominion,
            run_model_ids: Vec::new(),
            card_generation_pools: Vec::new(),
            transformation_pools: Vec::new(),
            map_col: 0,
            map_row: 0,
            act_index: 0,
            total_floor: 0,
        }),
        current_side: CombatSide::Player,
        round_number: 1,
        is_in_progress: false,
        is_starting: false,
        won: false,
        is_player_extra_turn: false,
        gold_was_stolen: false,
        end_player_turn_requested: false,
        card_action_boundary_depth: 0,
        next_creature_id: 2,
        initial_encounter_monster_count: 1,
        next_card_handle: (cards.len() + 1) as u32,
        next_creature_handle: 3,
        next_power_handle: 1,
        next_attack_id: 1,
        run_rngs,
        player_rngs,
        player: PlayerInst {
            creature_handle: pc,
            current_hp: 70,
            max_hp: 80,
            block: 0,
            cumulative_hp_lost: 0,
            gold: 99,
            max_energy: 3,
            base_orb_slot_count: 0,
            is_active_for_hooks: true,
            can_use_or_remove_potions: true,
            energy: 3,
            stars: 0,
            phase: PlayerTurnPhase::None,
            turn_number: 1,
            counters: PlayerCounters::default(),
            hand: SmallVec::new(),
            draw: SmallVec::new(),
            discard: SmallVec::new(),
            exhaust: SmallVec::new(),
            play: SmallVec::new(),
            deck,
            potion_slots: smallvec::smallvec![Some(PotionId::LiquidMemories), None, None],
            orb_capacity: 0,
            orbs: SmallVec::new(),
            arrows: SmallVec::new(),
            relics: smallvec::smallvec![sts2_kernel::RelicInst {
                id: RelicId::BurningBlood,
                is_wax: false,
                is_melted: false,
                stack_count: 1,
                floor_added_to_deck: None,
                private_fields: BTreeMap::new(),
            }],
            card_or_potion_effect_depth: 0,
        },
        cards,
        card_extensions: Vec::new(),
        creatures: vec![
            CreatureInst {
                handle: pc,
                combat_id: Some(0),
                side: CombatSide::Player,
                slot_name: None,
                is_player: true,
                pet_owner_player_index: None,
                monster_id: None,
                current_hp: 70,
                max_hp: 80,
                block: 0,
                cumulative_hp_lost: 0,
                is_detached: false,
                monster: None,
                powers: SmallVec::new(),
            },
            CreatureInst {
                handle: enemy,
                combat_id: Some(1),
                side: CombatSide::Enemy,
                slot_name: Some("slot_0".to_owned()),
                is_player: false,
                pet_owner_player_index: None,
                monster_id: Some(MonsterId::FuzzyWurmCrawler),
                current_hp: 50,
                max_hp: 50,
                block: 0,
                cumulative_hp_lost: 0,
                is_detached: false,
                monster: Some(MonsterInst {
                    monster_id: MonsterId::FuzzyWurmCrawler,
                    rng: Rng::from_seed(7),
                    is_performing_move: false,
                    spawned_this_turn: false,
                    current_state_id: "IDLE".to_owned(),
                    next_move_id: Some("IDLE".to_owned()),
                    next_move_can_transition_away: true,
                    performed_first_move: false,
                    state_log_ids: SmallVec::new(),
                    transient_move_follow_up_state_id: None,
                    setup_fields: BTreeMap::new(),
                    private_fields: BTreeMap::new(),
                }),
                powers: SmallVec::new(),
            },
        ],
        power_extensions: Vec::new(),
        ally_handles: smallvec::smallvec![pc],
        enemy_handles: smallvec::smallvec![enemy],
        escaped_handles: SmallVec::new(),
        removed_handles: SmallVec::new(),
        spawned_enemy_handles: smallvec::smallvec![enemy],
        pet_handles: SmallVec::new(),
        damage_history: Vec::new(),
        semantic_history: SemanticHistory::default(),
        pending: None,
        vakuu_auto_select_depth: 0,
    };
    (state, enemy)
}

#[test]
fn before_hand_draw_suspends_clones_and_resumes_into_hand() {
    let content = TestKernelContent::default();
    let (mut state, _enemy) = make_test_battle(Some(DominionId::KnowledgeDemonDominion), 7);
    let outcome = CombatEngine::start_combat(&mut state, &content).unwrap();
    assert!(matches!(outcome, StepOutcome::NeedsChoice(_)));
    assert_eq!(state.player.hand.len(), 0);

    let mut branch_a = state.clone();
    let res = CombatEngine::resume_choice(&mut branch_a, &content, &[0]).unwrap();
    assert_eq!(res, StepOutcome::Completed);
    assert_eq!(branch_a.player.discard.len(), 1);
    assert_eq!(branch_a.player.hand.len(), 5);
    assert_eq!(content.dominion_choices.borrow().as_slice(), &[1]);
}

#[test]
fn potion_choice_procure_and_automatic_lifecycle() {
    let content = TestKernelContent::default();
    let (mut state, _enemy) = make_test_battle(None, 7);
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Put two cards in discard so LiquidMemories triggers NeedsChoice
    let h0 = state.player.hand[0];
    let h1 = state.player.hand[1];
    card_pile::add_sync(&mut state, h0, PileType::Discard, CardPilePosition::Bottom);
    card_pile::add_sync(&mut state, h1, PileType::Discard, CardPilePosition::Bottom);

    let mut choices = SmallVec::new();
    let out = CombatEngine::use_potion(&mut state, &content, 0, None, &mut choices).unwrap();
    assert!(matches!(out, StepOutcome::NeedsChoice(_)));
    let res = CombatEngine::resume_choice(&mut state, &content, &[1]).unwrap();
    assert_eq!(res, StepOutcome::Completed);
    assert!(state.player.hand.contains(&h1));

    // Procure FairyInABottle into empty slot 0 and trigger automatic use
    assert!(potion::try_to_procure(&mut state, &content, PotionId::FairyInABottle).unwrap());
    assert_eq!(state.player.potion_slots[0], Some(PotionId::FairyInABottle));
    let auto_out = potion::use_automatic(&mut state, &content, 0, None).unwrap();
    assert_eq!(auto_out, StepOutcome::Completed);
    assert_eq!(state.player.current_hp, 80);
    assert!(content.potion_events.borrow().contains(&"after_procured"));
    assert!(content.potion_events.borrow().contains(&"after_used"));
}

#[test]
fn forge_autoplay_card_entry_and_enchantment_hooks_work() {
    let content = TestKernelContent::default();
    let (mut state, enemy) = make_test_battle(None, 6);
    // Enchant card #1 with Imbued (bottom on turn 1), card #2 with Glam+Sharp (replay +2 dmg)
    state.cards[0].ext_index = Some(0);
    state.card_extensions.push(CardExtension {
        enchantment: Some(EnchantmentInst {
            id: EnchantmentId::Imbued,
            amount: 1,
            status: EnchantmentStatus::Normal,
            private_fields: BTreeMap::new(),
        }),
        ..Default::default()
    });
    state.cards[1].ext_index = Some(1);
    state.card_extensions.push(CardExtension {
        enchantment: Some(EnchantmentInst {
            id: EnchantmentId::Glam,
            amount: 1,
            status: EnchantmentStatus::Normal,
            private_fields: BTreeMap::new(),
        }),
        ..Default::default()
    });

    CombatEngine::start_combat(&mut state, &content).unwrap();
    // Imbued card stayed at the bottom of draw pile (not drawn in opening 5)
    assert_eq!(
        state.player.draw.last().and_then(|&h| state.card(h)).and_then(|c| c.deck_card_handle),
        Some(CardHandle(1))
    );

    // Forge creates SovereignBlade in hand and adds +5 damage -> 15, plus fires AfterForge
    forge::forge(&mut state, &content, Dec::from_i32(5)).unwrap();
    let blade_h = *state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().id == CardId::SovereignBlade)
        .unwrap();
    assert_eq!(
        state
            .card_ext(state.card(blade_h).unwrap().ext_index)
            .unwrap()
            .private_fields
            .get("SovereignBlade._damage"),
        Some(&FieldValue::Decimal(Dec::from_i32(15)))
    );
    assert_eq!(content.forge_amounts.borrow().as_slice(), &[Dec::from_i32(5)]);
    assert!(content.entered_combat_cards.borrow().contains(&CardId::SovereignBlade));
    assert!(content.generated_cards.borrow().contains(&CardId::SovereignBlade));

    // Glam card plays twice and invokes enchantment_on_play twice
    if let Some(&glam_h) = state
        .player
        .hand
        .iter()
        .find(|&&h| state.card(h).unwrap().deck_card_handle == Some(CardHandle(2)))
    {
        let mut choices = SmallVec::new();
        CombatEngine::play_card(&mut state, &content, glam_h, Some(enemy), &mut choices).unwrap();
        assert_eq!(
            content
                .enchantment_plays
                .borrow()
                .iter()
                .filter(|&&e| e == EnchantmentId::Glam)
                .count(),
            2
        );
    }

    // AutoPlay top of draw pile
    let mut choices = SmallVec::new();
    let ap_out = autoplay::from_top_of_draw_pile(&mut state, &content, 1, true, &mut choices).unwrap();
    assert_eq!(ap_out, StepOutcome::Completed);

    // initial_encounter_monster_count & encounter_is_weak survive snapshot JSON round-trip
    let json = export_live_state(&state).unwrap();
    assert!(json.encounter_is_weak);
    assert_eq!(json.initial_encounter_monster_count, 1);
    let reimported = import_live_state(&json, &content).unwrap();
    assert!(reimported.static_ctx.encounter_is_weak);
    assert_eq!(reimported.initial_encounter_monster_count, 1);
}

#[derive(Default)]
struct PartBTestContent {
    pub events: std::cell::RefCell<Vec<String>>,
    pub pause_after_shuffle: std::cell::Cell<bool>,
    pub pause_after_player_turn_start: std::cell::Cell<bool>,
    pub enable_wave2_hooks: std::cell::Cell<bool>,
}

static MINION_META: PowerMeta = PowerMeta {
    power_type: PowerType::Buff,
    stack_type: PowerStackType::Single,
    instance_type: PowerInstanceType::None,
    flags: PowerStaticFlags::OWNER_IS_SECONDARY_ENEMY,
    hook_mask: HookMask::EMPTY.with(HookSlot::ShouldOwnerDeathTriggerFatal),
};

static LIFECYCLE_TEMP_POWER_META: PowerMeta = PowerMeta {
    power_type: PowerType::Debuff,
    stack_type: PowerStackType::Counter,
    instance_type: PowerInstanceType::None,
    flags: PowerStaticFlags::REMOVE_AFTER_OWNER_DEATH.union(PowerStaticFlags::IS_TEMPORARY),
    hook_mask: HookMask::EMPTY
        .with(HookSlot::BeforeApplied)
        .with(HookSlot::AfterApplied)
        .with(HookSlot::AfterRemoved)
        .with(HookSlot::AfterPowerAmountChanged)
        .with(HookSlot::ModifyWeakMultiplier),
};

static NORMAL_DEBUFF_META: PowerMeta = PowerMeta {
    power_type: PowerType::Debuff,
    stack_type: PowerStackType::Counter,
    instance_type: PowerInstanceType::None,
    flags: PowerStaticFlags::REMOVE_AFTER_OWNER_DEATH,
    hook_mask: HookMask::EMPTY
        .with(HookSlot::ModifyVulnerableMultiplier)
        .with(HookSlot::ShouldPowerBeRemovedOnDeath)
        .with(HookSlot::AfterShuffle)
        .with(HookSlot::AfterPlayerTurnStart)
        .with(HookSlot::ModifyBlockMultiplicative)
        .with(HookSlot::AfterModifyingBlockAmount),
};

static PART_B_RELIC_META: RelicMeta = RelicMeta {
    flags: sts2_types::RelicStaticFlags::empty(),
    hook_mask: HookMask::EMPTY
        .with(HookSlot::ModifyVulnerableMultiplier)
        .with(HookSlot::ModifyWeakMultiplier)
        .with(HookSlot::ShouldDraw)
        .with(HookSlot::AfterCardDrawn)
        .with(HookSlot::AfterCardExhausted)
        .with(HookSlot::ModifyCardPlayResultLocation)
        .with(HookSlot::AfterModifyingCardPlayResultLocation)
        .with(HookSlot::AfterCardGenerated)
        .with(HookSlot::AfterCardGeneratedForCombat)
        .with(HookSlot::AfterOrbEvoked)
        .with(HookSlot::AfterArrowShot)
        .with(HookSlot::AfterDamageReceived)
        .with(HookSlot::AfterAttack)
        .with(HookSlot::BeforePotionUsed)
        .with(HookSlot::ModifyXValue)
        .with(HookSlot::AfterPlayerTurnStartLate)
        .with(HookSlot::AfterAutoPrePlayPhaseEntered)
        .with(HookSlot::AfterAutoPrePlayPhaseEnteredLate)
        .with(HookSlot::ModifyBlockAdditive)
        .with(HookSlot::AfterModifyingBlockAmount)
        .with(HookSlot::ModifyOrbPassiveTriggerCounts)
        .with(HookSlot::AfterBlockBroken)
        .with(HookSlot::ModifyDamageAdditive)
        .with(HookSlot::ModifyDamageCap),
};

static PART_B_BURN_META: CardMeta = CardMeta {
    card_type: CardType::Status,
    rarity: CardRarity::Status,
    target_type: TargetType::None,
    canonical_energy_cost: -1,
    canonical_star_cost: -1,
    max_upgrade_level: 0,
    base_keywords: CardKeywords::UNPLAYABLE,
    base_tags: CardTags::empty(),
    flags: CardStaticFlags::HAS_TURN_END_IN_HAND,
    hook_mask: HookMask::EMPTY,
};

static PART_B_EXHAUST_META: CardMeta = CardMeta {
    card_type: CardType::Skill,
    rarity: CardRarity::Uncommon,
    target_type: TargetType::SelfTarget,
    canonical_energy_cost: 1,
    canonical_star_cost: -1,
    max_upgrade_level: 1,
    base_keywords: CardKeywords::EXHAUST,
    base_tags: CardTags::empty(),
    flags: CardStaticFlags::empty(),
    hook_mask: HookMask::EMPTY,
};

impl ContentDispatcher for PartBTestContent {
    fn card_meta(&self, id: CardId) -> Result<&'static CardMeta, KernelError> {
        match id {
            CardId::Burn => Ok(&PART_B_BURN_META),
            CardId::Mirage => Ok(&PART_B_EXHAUST_META),
            _ => Ok(sts2_types::generated_card_meta(id)),
        }
    }

    fn power_meta(&self, id: PowerId) -> Result<&'static PowerMeta, KernelError> {
        match id {
            PowerId::MinionPower => Ok(&MINION_META),
            PowerId::TemporaryStrengthPower => Ok(&LIFECYCLE_TEMP_POWER_META),
            _ => Ok(&NORMAL_DEBUFF_META),
        }
    }

    fn relic_meta(&self, _id: RelicId) -> Result<&'static RelicMeta, KernelError> {
        Ok(&PART_B_RELIC_META)
    }

    fn potion_meta(&self, _id: PotionId) -> Result<&'static PotionMeta, KernelError> {
        Ok(&TEST_POTION_META)
    }

    fn monster_meta(&self, _id: MonsterId) -> Result<&'static MonsterMeta, KernelError> {
        Ok(&TEST_MONSTER_META)
    }

    fn orb_meta(&self, _id: OrbId) -> Result<&'static OrbMeta, KernelError> {
        Ok(&TEST_ORB_META)
    }

    fn card_is_playable(&self, _state: &BattleState, _card: CardHandle) -> Result<bool, KernelError> {
        Ok(true)
    }

    fn card_on_play(
        &self,
        state: &mut BattleState,
        ctx: &CardPlayContext,
        _choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
        _resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        if let Some(target) = ctx.target {
            let owner = state.player.creature_handle;
            damage::AttackBuilder::from_card(
                owner,
                ctx.card,
                *ctx,
                Dec::from_i32(4),
                damage::AttackTargetSpec::Single(target),
            )
            .execute(state, self)?;
        }
        let mut outcome = CardPlayOutcome::default();
        if self.enable_wave2_hooks.get()
            && state.card(ctx.card).is_some_and(|c| c.id == CardId::MourningBlow)
        {
            outcome.transform_into = Some(CardId::Sadness);
        }
        Ok(outcome)
    }

    fn card_on_play_resume_after_select(
        &self,
        _state: &mut BattleState,
        _ctx: &CardPlayContext,
        _selected: &[CardHandle],
        _resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        Ok(CardPlayOutcome::default())
    }

    fn card_on_upgrade(&self, state: &mut BattleState, card: CardHandle) -> Result<(), KernelError> {
        if let Some(c) = state.card_mut(card) {
            c.base_energy_cost = 0;
        }
        Ok(())
    }

    fn card_on_downgrade(&self, _state: &mut BattleState, card: CardHandle) -> Result<(), KernelError> {
        self.events.borrow_mut().push(format!("downgrade:{}", card.0));
        Ok(())
    }

    fn card_comparison_base_values(
        &self,
        _state: &BattleState,
        _card: CardHandle,
    ) -> Result<SmallVec<[(&'static str, Dec); 4]>, KernelError> {
        Ok(SmallVec::new())
    }

    fn card_on_turn_end_in_hand(
        &self,
        state: &mut BattleState,
        card: CardHandle,
    ) -> Result<(), KernelError> {
        let id = state.card(card).unwrap().id;
        self.events.borrow_mut().push(format!("turn_end_in_hand:{id:?}"));
        Ok(())
    }

    fn card_resolved_type(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<CardType, KernelError> {
        let id = state.card(card).unwrap().id;
        if self.enable_wave2_hooks.get() && id == CardId::MadScience {
            return Ok(CardType::Power);
        }
        Ok(self.card_meta(id)?.card_type)
    }

    fn card_resolved_target_type(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<TargetType, KernelError> {
        let id = state.card(card).unwrap().id;
        if self.enable_wave2_hooks.get() && id == CardId::MadScience {
            return Ok(TargetType::SelfTarget);
        }
        Ok(self.card_meta(id)?.target_type)
    }

    fn card_default_result_location(
        &self,
        state: &BattleState,
        card: CardHandle,
        _is_auto_play: bool,
    ) -> Result<Option<(PileType, CardPilePosition)>, KernelError> {
        let id = state.card(card).unwrap().id;
        if self.enable_wave2_hooks.get() && id == CardId::MourningBlow {
            return Ok(Some((PileType::Hand, CardPilePosition::Bottom)));
        }
        Ok(None)
    }

    fn card_thrash_damage_variable(
        &self,
        _state: &BattleState,
        _card: CardHandle,
    ) -> Result<Dec, KernelError> {
        Ok(Dec::from_i32(12))
    }

    fn monster_initial_hp_range(
        &self,
        _state: &BattleState,
        _monster_id: MonsterId,
    ) -> Result<(i32, i32), KernelError> {
        Ok((25, 25))
    }

    fn monster_setup_for_combat(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        if let Some(c) = state.creature_mut(creature) {
            if let Some(m) = &mut c.monster {
                m.current_state_id = "IDLE".to_owned();
                m.next_move_id = Some("IDLE".to_owned());
            }
        }
        Ok(())
    }

    fn monster_after_added_to_room(
        &self,
        _state: &mut BattleState,
        _creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        Ok(())
    }

    fn monster_roll_move(
        &self,
        _state: &mut BattleState,
        _creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        Ok(())
    }

    fn monster_perform_move(
        &self,
        _state: &mut BattleState,
        _creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        Ok(())
    }

    fn orb_passive(
        &self,
        _state: &mut BattleState,
        _orb_idx: usize,
        _target: Option<CreatureHandle>,
    ) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
        Ok(SmallVec::new())
    }

    fn orb_evoke(
        &self,
        state: &mut BattleState,
        _orb_idx: usize,
    ) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
        Ok(state.enemy_handles.iter().copied().collect())
    }

    fn orb_before_turn_end(&self, _state: &mut BattleState, _orb_idx: usize) -> Result<(), KernelError> {
        Ok(())
    }

    fn orb_after_turn_start(&self, _state: &mut BattleState, _orb_idx: usize) -> Result<(), KernelError> {
        Ok(())
    }

    fn potion_on_use(
        &self,
        _state: &mut BattleState,
        _potion_id: PotionId,
        _target: Option<CreatureHandle>,
        _choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
        _resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        Ok(CardPlayOutcome::default())
    }

    fn potion_on_use_resume_after_select(
        &self,
        _state: &mut BattleState,
        _potion_id: PotionId,
        _target: Option<CreatureHandle>,
        _selected: &[CardHandle],
        _resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        Ok(CardPlayOutcome::default())
    }

    fn resume_hook_choice(
        &self,
        state: &mut BattleState,
        _listener: HookListener,
        slot: HookSlot,
        selected: &[CardHandle],
    ) -> Result<HookResult, KernelError> {
        self.events.borrow_mut().push(format!("resume_hook:{slot:?}:{}", selected.len()));
        if slot == HookSlot::AfterShuffle {
            for &h in selected {
                card_pile::add_sync(state, h, PileType::Hand, CardPilePosition::Bottom);
            }
        } else if slot == HookSlot::AfterPlayerTurnStart {
            for &h in selected {
                card_pile::exhaust(state, self, h, false)?;
            }
        }
        Ok(HookResult::default())
    }

    fn enchantment_on_play(
        &self,
        _state: &mut BattleState,
        _card: CardHandle,
        _ctx: &CardPlayContext,
    ) -> Result<(), KernelError> {
        Ok(())
    }

    fn affliction_on_play(
        &self,
        _state: &mut BattleState,
        _card: CardHandle,
        _ctx: &CardPlayContext,
    ) -> Result<(), KernelError> {
        Ok(())
    }

    fn enchantment_play_count(
        &self,
        _state: &BattleState,
        _card: CardHandle,
        play_count: i32,
    ) -> Result<i32, KernelError> {
        Ok(play_count)
    }

    fn enchantment_damage_additive(
        &self,
        _state: &BattleState,
        _card: CardHandle,
        _amount: Dec,
        _props: ValueProp,
    ) -> Result<Dec, KernelError> {
        Ok(Dec::ZERO)
    }

    fn enchantment_damage_multiplicative(
        &self,
        _state: &BattleState,
        _card: CardHandle,
        _amount: Dec,
        _props: ValueProp,
    ) -> Result<Dec, KernelError> {
        Ok(Dec::ONE)
    }

    fn enchantment_should_start_at_bottom(&self, _id: EnchantmentId) -> Result<bool, KernelError> {
        Ok(false)
    }

    fn enchantment_modify_shuffle_order(
        &self,
        _state: &BattleState,
        _card: CardHandle,
        _cards: &mut Vec<CardHandle>,
        _is_initial_shuffle: bool,
    ) -> Result<(), KernelError> {
        Ok(())
    }

    fn listener_hook_mask(
        &self,
        state: &BattleState,
        listener: HookListener,
    ) -> Result<HookMask, KernelError> {
        match listener {
            HookListener::Relic(_) => Ok(PART_B_RELIC_META.hook_mask),
            HookListener::Power { creature, power_handle } => {
                let id = state
                    .creature(creature)
                    .and_then(|c| c.powers.iter().find(|p| p.handle == power_handle))
                    .unwrap()
                    .id;
                Ok(self.power_meta(id)?.hook_mask)
            }
            HookListener::DetachedPower { power, .. } => Ok(self.power_meta(power.id)?.hook_mask),
            _ => Ok(HookMask::EMPTY),
        }
    }

    fn dispatch_hook(
        &self,
        state: &mut BattleState,
        listener: HookListener,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<HookResult, KernelError> {
        match slot {
            HookSlot::BeforeApplied => {
                if let HookArgs::PowerChanged { power_id, power_handle, owner, .. } = args {
                    let already_attached = state
                        .creature(*owner)
                        .is_some_and(|c| c.powers.iter().any(|p| p.handle == *power_handle));
                    self.events
                        .borrow_mut()
                        .push(format!("before_applied:{power_id:?}:attached={already_attached}:h={power_handle}"));
                }
            }
            HookSlot::AfterApplied => {
                if let HookArgs::PowerChanged { power_id, power_handle, owner, .. } = args {
                    let attached = state
                        .creature(*owner)
                        .is_some_and(|c| c.powers.iter().any(|p| p.handle == *power_handle));
                    self.events
                        .borrow_mut()
                        .push(format!("after_applied:{power_id:?}:attached={attached}:h={power_handle}"));
                }
            }
            HookSlot::AfterRemoved => {
                if let HookArgs::PowerChanged { power_id, power_handle, owner, amount, .. } = args {
                    let still_attached = state
                        .creature(*owner)
                        .is_some_and(|c| c.powers.iter().any(|p| p.handle == *power_handle));
                    self.events
                        .borrow_mut()
                        .push(format!("after_removed:{power_id:?}:attached={still_attached}:amt={amount}:h={power_handle}"));
                }
            }
            HookSlot::AfterPowerAmountChanged => {
                if let HookArgs::PowerChanged { power_id, power_handle, .. } = args {
                    self.events
                        .borrow_mut()
                        .push(format!("power_changed:{power_id:?}:h={power_handle}"));
                }
            }
            HookSlot::ShouldOwnerDeathTriggerFatal => {
                if let HookListener::Power { .. } = listener {
                    return Ok(HookResult {
                        bool_val: Some(false),
                        ..Default::default()
                    });
                }
            }
            HookSlot::ShouldPowerBeRemovedOnDeath => {
                if let HookArgs::PowerQuery { power_id, is_temporary, power_handle, .. } = args {
                    self.events
                        .borrow_mut()
                        .push(format!("death_query:{power_id:?}:temp={is_temporary}:h={power_handle}"));
                    if *is_temporary {
                        return Ok(HookResult {
                            bool_val: Some(false),
                            ..Default::default()
                        });
                    }
                }
            }
            HookSlot::ModifyVulnerableMultiplier => {
                if let HookArgs::ModifyDamage { amount, .. } = args {
                    return Ok(HookResult {
                        dec_val: Some(amount.checked_add(Dec::from_i32(25).checked_div(Dec::from_i32(100))?)?),
                        ..Default::default()
                    });
                }
            }
            HookSlot::ModifyWeakMultiplier => {
                if let HookArgs::ModifyDamage { amount, .. } = args {
                    return Ok(HookResult {
                        dec_val: Some(amount.checked_sub(Dec::from_i32(15).checked_div(Dec::from_i32(100))?)?),
                        ..Default::default()
                    });
                }
            }
            HookSlot::ShouldDraw => {
                if let HookArgs::ShouldDraw { from_hand_draw } = args {
                    self.events.borrow_mut().push(format!("should_draw:{from_hand_draw}"));
                }
            }
            HookSlot::AfterCardDrawn => {
                if let HookArgs::CardDrawn { from_hand_draw, .. } = args {
                    self.events.borrow_mut().push(format!("card_drawn:{from_hand_draw}"));
                }
            }
            HookSlot::AfterCardExhausted => {
                if let HookArgs::CardExhausted { caused_by_ethereal, .. } = args {
                    self.events.borrow_mut().push(format!("exhaust:{caused_by_ethereal}"));
                }
            }
            HookSlot::ModifyCardPlayResultLocation => {
                if let HookArgs::CardPlayResultLocation { energy_value, .. } = args {
                    self.events.borrow_mut().push(format!("mod_loc:e={energy_value}"));
                }
            }
            HookSlot::AfterCardGenerated => {
                if let HookArgs::CardGenerated { creator, .. } = args {
                    self.events.borrow_mut().push(format!("gen:{}", creator.is_some()));
                }
            }
            HookSlot::AfterCardGeneratedForCombat => {
                if let HookArgs::CardGenerated { creator, .. } = args {
                    self.events.borrow_mut().push(format!("gen_combat:{}", creator.is_some()));
                }
            }
            HookSlot::AfterOrbEvoked => {
                if let HookArgs::OrbEvoked { targets, .. } = args {
                    self.events.borrow_mut().push(format!("orb_evoked:targets={}", targets.len()));
                }
            }
            HookSlot::AfterArrowShot => {
                if let HookArgs::ArrowShot { arrow_idx, .. } = args {
                    self.events.borrow_mut().push(format!("arrow_shot:{arrow_idx:?}"));
                }
            }
            HookSlot::AfterDamageReceived => {
                if let HookArgs::DamageResult { blocked_damage, was_fully_blocked, .. } = args {
                    self.events.borrow_mut().push(format!(
                        "dmg_result:blocked={blocked_damage}:full={was_fully_blocked}"
                    ));
                }
            }
            HookSlot::AfterAttack => {
                if let HookArgs::Attack { info, results } = args {
                    self.events.borrow_mut().push(format!(
                        "after_attack:side={:?}:hits={}",
                        info.target_side,
                        results.len()
                    ));
                }
            }
            HookSlot::BeforePotionUsed => {
                if let HookArgs::Potion { target, .. } = args {
                    self.events.borrow_mut().push(format!("before_potion:target={}", target.is_some()));
                }
            }
            HookSlot::ModifyXValue if self.enable_wave2_hooks.get() => {
                if let HookArgs::ModifyXValue { amount, .. } = args {
                    return Ok(HookResult {
                        int_val: Some(amount + 2),
                        ..Default::default()
                    });
                }
            }
            HookSlot::AfterPlayerTurnStartLate if self.enable_wave2_hooks.get() => {
                self.events.borrow_mut().push("after_turn_start_late".to_owned());
            }
            HookSlot::AfterAutoPrePlayPhaseEntered if self.enable_wave2_hooks.get() => {
                self.events.borrow_mut().push("after_auto_pre_play".to_owned());
            }
            HookSlot::AfterAutoPrePlayPhaseEnteredLate if self.enable_wave2_hooks.get() => {
                self.events.borrow_mut().push("after_auto_pre_play_late".to_owned());
            }
            HookSlot::ModifyBlockAdditive if self.enable_wave2_hooks.get() => {
                if let HookListener::Relic(0) = listener {
                    return Ok(HookResult {
                        dec_val: Some(Dec::from_i32(2)),
                        ..Default::default()
                    });
                }
            }
            HookSlot::ModifyBlockMultiplicative if self.enable_wave2_hooks.get() => {
                if let HookListener::Power { .. } = listener {
                    return Ok(HookResult {
                        dec_val: Some(Dec::TWO),
                        ..Default::default()
                    });
                }
            }
            HookSlot::AfterModifyingBlockAmount if self.enable_wave2_hooks.get() => {
                if let HookArgs::ModifyBlock { target, amount, .. } = args {
                    self.events.borrow_mut().push(format!(
                        "after_mod_block:{:?}:t={}:amt={}",
                        listener, target.0, amount
                    ));
                }
            }
            HookSlot::ModifyOrbPassiveTriggerCounts if self.enable_wave2_hooks.get() => {
                if let HookListener::Relic(0) = listener {
                    if let HookArgs::ModifyOrbPassiveTriggerCount {
                        orb_idx,
                        trigger_count,
                        ..
                    } = args
                    {
                        if *orb_idx == 0 {
                            return Ok(HookResult {
                                int_val: Some(*trigger_count + 1),
                                ..Default::default()
                            });
                        }
                    }
                }
            }
            HookSlot::AfterBlockBroken if self.enable_wave2_hooks.get() => {
                if let HookArgs::BlockBroken { target, breaker } = args {
                    self.events
                        .borrow_mut()
                        .push(format!("block_broken:t={}:b={:?}", target.0, breaker.map(|h| h.0)));
                }
            }
            HookSlot::ModifyDamageAdditive if self.enable_wave2_hooks.get() => {
                return Ok(HookResult {
                    dec_val: Some(Dec::from_i32(100)),
                    ..Default::default()
                });
            }
            HookSlot::ModifyDamageCap if self.enable_wave2_hooks.get() => {
                return Ok(HookResult {
                    dec_val: Some(Dec::from_i32(7)),
                    ..Default::default()
                });
            }
            HookSlot::AfterShuffle if self.pause_after_shuffle.get() && !state.player.draw.is_empty() => {
                self.pause_after_shuffle.set(false);
                let candidates: SmallVec<[CardHandle; 16]> = state.player.draw.iter().take(2).copied().collect();
                return Ok(HookResult {
                    needs_choice: Some(ChoiceRequest {
                        timing: ChoiceTiming::Action,
                        source_id: "POWER.STRATAGEM_POWER".to_owned(),
                        source_pile: PileType::Draw,
                        min_count: 1,
                        max_count: 1,
                        candidates,
                        can_skip: false,
                    }),
                    ..Default::default()
                });
            }
            HookSlot::AfterPlayerTurnStart
                if self.pause_after_player_turn_start.get() && !state.player.hand.is_empty() =>
            {
                self.pause_after_player_turn_start.set(false);
                let candidates: SmallVec<[CardHandle; 16]> = state.player.hand.iter().take(3).copied().collect();
                return Ok(HookResult {
                    needs_choice: Some(ChoiceRequest {
                        timing: ChoiceTiming::PlayerTurnStart,
                        source_id: "POWER.TOOLS_OF_THE_TRADE_POWER".to_owned(),
                        source_pile: PileType::Hand,
                        min_count: 1,
                        max_count: 1,
                        candidates,
                        can_skip: false,
                    }),
                    ..Default::default()
                });
            }
            _ => {}
        }
        Ok(HookResult::default())
    }
}

#[test]
fn secondary_enemy_power_lifecycle_and_multiplier_hooks() {
    let content = PartBTestContent::default();
    let (mut state, enemy) = make_test_battle(None, 6);
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Spawn a second primary enemy so turning `enemy` into a Minion doesn't end combat
    let enemy2 = creature::spawn_monster_in_combat(
        &mut state,
        &content,
        MonsterId::FuzzyWurmCrawler,
        CombatSide::Enemy,
        None,
    )
    .unwrap();

    // DieForYouPower does NOT make an enemy secondary; MinionPower DOES
    power::apply(&mut state, &content, PowerId::DieForYouPower, enemy, Dec::ONE, None, None).unwrap();
    assert!(!state.creature(enemy).unwrap().is_secondary_enemy());
    assert!(sts2_kernel::hooks::should_owner_death_trigger_fatal(&mut state, &content, enemy).unwrap());

    power::apply(&mut state, &content, PowerId::MinionPower, enemy, Dec::ONE, None, None).unwrap();
    assert!(state.creature(enemy).unwrap().is_secondary_enemy());
    assert!(!sts2_kernel::hooks::should_owner_death_trigger_fatal(&mut state, &content, enemy).unwrap());

    // Power lifecycle: BeforeApplied -> AfterApplied -> AfterPowerAmountChanged -> AfterRemoved
    let pc = state.player.creature_handle;
    power::apply(
        &mut state,
        &content,
        PowerId::TemporaryStrengthPower,
        pc,
        Dec::from_i32(3),
        Some(pc),
        None,
    )
    .unwrap();
    let temp_h = state.player_creature().unwrap().powers[0].handle;

    // Apply both temporary + normal powers on enemy2 to check per-power ShouldPowerBeRemovedOnDeath
    power::apply(&mut state, &content, PowerId::TemporaryStrengthPower, enemy2, Dec::TWO, None, None).unwrap();
    power::apply(&mut state, &content, PowerId::VulnerablePower, enemy2, Dec::TWO, None, None).unwrap();

    // Check Vulnerable and Weak multiplier hooks
    let vuln_mult = sts2_kernel::hooks::modify_vulnerable_multiplier(
        &mut state,
        &content,
        Some(enemy2),
        Some(pc),
        Dec::ONE_POINT_FIVE,
        ValueProp::MOVE,
        None,
    )
    .unwrap();
    assert_eq!(vuln_mult, Dec::from_i32(2));
    let weak_mult = sts2_kernel::hooks::modify_weak_multiplier(
        &mut state,
        &content,
        Some(enemy2),
        Some(pc),
        Dec::THREE_QUARTERS,
        ValueProp::MOVE,
        None,
    )
    .unwrap();
    assert_eq!(
        weak_mult,
        Dec::from_i32(60).checked_div(Dec::from_i32(100)).unwrap()
    );

    // Killing primary enemy2 keeps TemporaryStrengthPower on enemy2, removes VulnerablePower, and auto-kills minion `enemy`
    creature::kill(&mut state, &content, enemy2, None).unwrap();
    let e2 = state.creature(enemy2).unwrap();
    assert!(e2.powers.iter().any(|p| p.id == PowerId::TemporaryStrengthPower));
    assert!(!e2.powers.iter().any(|p| p.id == PowerId::VulnerablePower));
    assert!(state.creature(enemy).unwrap().is_dead());

    let removed = power::remove_by_handle(&mut state, &content, pc, temp_h).unwrap();
    assert!(removed.is_some());
    let evs = content.events.borrow();
    assert!(evs.iter().any(|e| e.starts_with("before_applied:TemporaryStrengthPower:attached=false")));
    assert!(evs.iter().any(|e| e.starts_with("after_applied:TemporaryStrengthPower:attached=true")));
    assert!(evs.iter().any(|e| e.starts_with("power_changed:TemporaryStrengthPower:h=")));
    assert!(evs.iter().any(|e| e.starts_with("after_removed:TemporaryStrengthPower:attached=false:amt=3")));
}

#[test]
fn hook_card_select_after_shuffle_and_after_player_turn_start_clone_and_resume() {
    let content = PartBTestContent::default();
    let (mut state, _enemy) = make_test_battle(None, 7);
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Attach Stratagem-like AfterShuffle + Tools-like AfterPlayerTurnStart listener to player
    let pc = state.player.creature_handle;
    power::apply(&mut state, &content, PowerId::ToolsOfTheTradePower, pc, Dec::ONE, None, None).unwrap();

    // Put all 7 cards in discard + 2 in draw so next 5-card hand draw triggers shuffle mid-draw!
    let hand_cards: Vec<CardHandle> = state.player.hand.iter().copied().collect();
    for h in hand_cards {
        card_pile::add_sync(&mut state, h, PileType::Discard, CardPilePosition::Bottom);
    }

    content.pause_after_shuffle.set(true);
    content.pause_after_player_turn_start.set(true);
    let outcome = CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert!(matches!(outcome, StepOutcome::NeedsChoice(ref r) if r.source_id == "POWER.STRATAGEM_POWER"));

    // Clone before resuming AfterShuffle and finish turn start through AfterPlayerTurnStart pause
    let mut clone_b = state.clone();
    let mid_outcome = CombatEngine::resume_choice(&mut clone_b, &content, &[0]).unwrap();
    assert!(
        matches!(mid_outcome, StepOutcome::NeedsChoice(ref r) if r.source_id == "POWER.TOOLS_OF_THE_TRADE_POWER")
    );
    // Original state unchanged after cloning
    assert!(state.pending.is_some());

    let hand_before = clone_b.player.hand.len();
    let final_outcome = CombatEngine::resume_choice(&mut clone_b, &content, &[0]).unwrap();
    assert_eq!(final_outcome, StepOutcome::Completed);
    assert_eq!(clone_b.player.phase, PlayerTurnPhase::Play);
    assert_eq!(clone_b.player.exhaust.len(), 1);
    assert_eq!(clone_b.player.hand.len(), hand_before - 1);
}

#[test]
fn creature_card_factory_autoplay_and_hook_args_payloads_work() {
    let content = PartBTestContent::default();
    let (mut state, enemy) = make_test_battle(None, 8);
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // Creature stun, set_max_and_current_hp, spawn_monster_in_combat
    creature::stun(&mut state, enemy, Some("FOLLOW_UP")).unwrap();
    assert_eq!(
        state.creature(enemy).unwrap().monster.as_ref().unwrap().next_move_id.as_deref(),
        Some("STUNNED")
    );
    creature::set_max_and_current_hp(&mut state, &content, enemy, Dec::from_i32(30)).unwrap();
    assert_eq!(state.creature(enemy).unwrap().max_hp, 30);
    assert_eq!(state.creature(enemy).unwrap().current_hp, 30);

    // Afflict, downgrade, transform, transform_to_random
    let card0 = state.player.hand[0];
    assert!(card::afflict(&mut state, card0, AfflictionId::Bound, 1));
    assert_eq!(state.player.hand.len(), 5);
    card::upgrade(&mut state, &content, card0).unwrap();
    assert_eq!(state.card(card0).unwrap().upgrade_level, 1);
    card::downgrade(&mut state, &content, card0).unwrap();
    assert_eq!(state.card(card0).unwrap().upgrade_level, 0);
    assert_eq!(state.card(card0).unwrap().base_energy_cost, 1);

    let rep = card::transform(&mut state, &content, card0, CardId::PommelStrike).unwrap().unwrap();
    assert_eq!(state.card(rep).unwrap().id, CardId::PommelStrike);
    let rand_rep = card::transform_to_random(&mut state, &content, rep, RunRngType::CombatCardGeneration)
        .unwrap()
        .unwrap();
    assert_ne!(state.card(rand_rep).unwrap().id, CardId::PommelStrike);

    // CardFactory pools + Tarot + Blitz filter
    let pool = card_factory::character_combat_pool(&state);
    assert!(!pool.is_empty());
    let picked = card_factory::get_distinct_for_combat(&mut state, &pool, 3, RunRngType::CombatCardGeneration);
    assert_eq!(picked.len(), 3);
    assert_eq!(card_factory::tarot_cards().len(), 22);
    let mut blitz_ctx = (*state.static_ctx).clone();
    blitz_ctx.game_mode = GameMode::Blitz;
    state.static_ctx = Arc::new(blitz_ctx);
    assert!(card_factory::is_card_allowed_in_run(&state, CardId::StrikeIronclad));

    // Exact HookArgs payloads: DamageResult blocked/fully_blocked, Attack results/target_side,
    // CardExhausted ethereal, OrbEvoked targets, ArrowShot idx, BeforePotionUsed target
    creature::gain_block_internal(&mut state, enemy, Dec::from_i32(10)).unwrap();
    let mut choices = SmallVec::new();
    let pc_h = state.player.creature_handle;
    let attack_card = card_pile::add_generated_card_to_combat(
        &mut state,
        &content,
        CardId::StrikeIronclad,
        PileType::Hand,
        CardPilePosition::Bottom,
        0,
        Some(pc_h),
    )
    .unwrap();
    CombatEngine::play_card(&mut state, &content, attack_card, Some(enemy), &mut choices).unwrap();

    // Autoplay from_cards with random enemy + Unplayable Burn
    let burn = card_pile::add_generated_card_to_combat(
        &mut state,
        &content,
        CardId::Burn,
        PileType::Hand,
        CardPilePosition::Bottom,
        0,
        None,
    )
    .unwrap();
    let s2 = card_pile::add_generated_card_to_combat(
        &mut state,
        &content,
        CardId::StrikeIronclad,
        PileType::Hand,
        CardPilePosition::Bottom,
        0,
        None,
    )
    .unwrap();
    autoplay::from_cards(&mut state, &content, &[burn, s2], None, false).unwrap();

    // Ethereal exhaust + Orb evoke + Arrow shot + Potion before_used
    card_pile::exhaust(&mut state, &content, rand_rep, true).unwrap();
    state.player.orb_capacity = 3;
    orb::channel(&mut state, &content, OrbId::LightningOrb).unwrap();
    orb::evoke_next(&mut state, &content, true).unwrap();
    sts2_kernel::commands::arrow::gain_arrow_slot(&mut state);
    sts2_kernel::commands::arrow::try_shoot_arrow(&mut state, &content, Some(s2)).unwrap();
    CombatEngine::use_potion(&mut state, &content, 0, Some(enemy), &mut choices).unwrap();

    let evs = content.events.borrow();
    assert!(evs.iter().any(|e| e == "dmg_result:blocked=4:full=true"));
    assert!(evs.iter().any(|e| e == "after_attack:side=Enemy:hits=1"));
    assert!(evs.iter().any(|e| e == "mod_loc:e=1"));
    assert!(evs.iter().any(|e| e == "gen:true"));
    assert!(evs.iter().any(|e| e == "gen_combat:true"));
    assert!(evs.iter().any(|e| e == "exhaust:true"));
    assert!(evs.iter().any(|e| e == "orb_evoked:targets=1"));
    assert!(evs.iter().any(|e| e == "arrow_shot:Some(0)"));
    assert!(evs.iter().any(|e| e == "before_potion:target=true"));
    assert!(evs.iter().any(|e| e == "should_draw:true"));
    assert!(evs.iter().any(|e| e == "card_drawn:true"));
}

#[derive(Default)]
struct FailOnMoveContent {
    base: PartBTestContent,
}

impl ContentDispatcher for FailOnMoveContent {
    fn card_meta(&self, id: CardId) -> Result<&'static CardMeta, KernelError> {
        self.base.card_meta(id)
    }
    fn power_meta(&self, id: PowerId) -> Result<&'static PowerMeta, KernelError> {
        self.base.power_meta(id)
    }
    fn relic_meta(&self, id: RelicId) -> Result<&'static RelicMeta, KernelError> {
        self.base.relic_meta(id)
    }
    fn potion_meta(&self, id: PotionId) -> Result<&'static PotionMeta, KernelError> {
        self.base.potion_meta(id)
    }
    fn monster_meta(&self, id: MonsterId) -> Result<&'static MonsterMeta, KernelError> {
        self.base.monster_meta(id)
    }
    fn orb_meta(&self, id: OrbId) -> Result<&'static OrbMeta, KernelError> {
        self.base.orb_meta(id)
    }
    fn card_is_playable(&self, state: &BattleState, card: CardHandle) -> Result<bool, KernelError> {
        self.base.card_is_playable(state, card)
    }
    fn card_on_play(
        &self,
        state: &mut BattleState,
        ctx: &CardPlayContext,
        choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        self.base.card_on_play(state, ctx, choice_queue, resume_step)
    }
    fn card_on_play_resume_after_select(
        &self,
        state: &mut BattleState,
        ctx: &CardPlayContext,
        selected: &[CardHandle],
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        self.base
            .card_on_play_resume_after_select(state, ctx, selected, resume_step)
    }
    fn card_on_upgrade(&self, state: &mut BattleState, card: CardHandle) -> Result<(), KernelError> {
        self.base.card_on_upgrade(state, card)
    }
    fn card_comparison_base_values(
        &self,
        state: &BattleState,
        card: CardHandle,
    ) -> Result<SmallVec<[(&'static str, Dec); 4]>, KernelError> {
        self.base.card_comparison_base_values(state, card)
    }
    fn monster_initial_hp_range(
        &self,
        state: &BattleState,
        monster_id: MonsterId,
    ) -> Result<(i32, i32), KernelError> {
        self.base.monster_initial_hp_range(state, monster_id)
    }
    fn monster_setup_for_combat(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        self.base.monster_setup_for_combat(state, creature)
    }
    fn monster_after_added_to_room(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        self.base.monster_after_added_to_room(state, creature)
    }
    fn monster_roll_move(
        &self,
        _state: &mut BattleState,
        _creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        Err(KernelError::Unsupported {
            kind: "Monster",
            id: "should_not_roll_when_stunned".to_owned(),
        })
    }
    fn monster_perform_move(
        &self,
        state: &mut BattleState,
        creature: CreatureHandle,
    ) -> Result<(), KernelError> {
        let is_stunned = state
            .creature(creature)
            .and_then(|c| c.monster.as_ref())
            .is_some_and(|m| m.next_move_id.as_deref() == Some("STUNNED"));
        if is_stunned {
            return Ok(());
        }
        Err(KernelError::Unsupported {
            kind: "Monster",
            id: "should_only_perform_stunned_move".to_owned(),
        })
    }
    fn orb_passive(
        &self,
        state: &mut BattleState,
        orb_idx: usize,
        target: Option<CreatureHandle>,
    ) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
        self.base.orb_passive(state, orb_idx, target)
    }
    fn orb_evoke(
        &self,
        state: &mut BattleState,
        orb_idx: usize,
    ) -> Result<SmallVec<[CreatureHandle; 4]>, KernelError> {
        self.base.orb_evoke(state, orb_idx)
    }
    fn orb_before_turn_end(&self, state: &mut BattleState, orb_idx: usize) -> Result<(), KernelError> {
        self.base.orb_before_turn_end(state, orb_idx)
    }
    fn orb_after_turn_start(&self, state: &mut BattleState, orb_idx: usize) -> Result<(), KernelError> {
        self.base.orb_after_turn_start(state, orb_idx)
    }
    fn potion_on_use(
        &self,
        state: &mut BattleState,
        potion_id: PotionId,
        target: Option<CreatureHandle>,
        choice_queue: &mut SmallVec<[Vec<usize>; 2]>,
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        self.base
            .potion_on_use(state, potion_id, target, choice_queue, resume_step)
    }
    fn potion_on_use_resume_after_select(
        &self,
        state: &mut BattleState,
        potion_id: PotionId,
        target: Option<CreatureHandle>,
        selected: &[CardHandle],
        resume_step: u8,
    ) -> Result<CardPlayOutcome, KernelError> {
        self.base
            .potion_on_use_resume_after_select(state, potion_id, target, selected, resume_step)
    }
    fn resume_hook_choice(
        &self,
        state: &mut BattleState,
        listener: HookListener,
        slot: HookSlot,
        selected: &[CardHandle],
    ) -> Result<HookResult, KernelError> {
        self.base.resume_hook_choice(state, listener, slot, selected)
    }
    fn enchantment_on_play(
        &self,
        state: &mut BattleState,
        card: CardHandle,
        ctx: &CardPlayContext,
    ) -> Result<(), KernelError> {
        self.base.enchantment_on_play(state, card, ctx)
    }
    fn affliction_on_play(
        &self,
        state: &mut BattleState,
        card: CardHandle,
        ctx: &CardPlayContext,
    ) -> Result<(), KernelError> {
        self.base.affliction_on_play(state, card, ctx)
    }
    fn enchantment_play_count(
        &self,
        state: &BattleState,
        card: CardHandle,
        play_count: i32,
    ) -> Result<i32, KernelError> {
        self.base.enchantment_play_count(state, card, play_count)
    }
    fn enchantment_damage_additive(
        &self,
        state: &BattleState,
        card: CardHandle,
        amount: Dec,
        props: ValueProp,
    ) -> Result<Dec, KernelError> {
        self.base.enchantment_damage_additive(state, card, amount, props)
    }
    fn enchantment_damage_multiplicative(
        &self,
        state: &BattleState,
        card: CardHandle,
        amount: Dec,
        props: ValueProp,
    ) -> Result<Dec, KernelError> {
        self.base
            .enchantment_damage_multiplicative(state, card, amount, props)
    }
    fn enchantment_should_start_at_bottom(&self, id: EnchantmentId) -> Result<bool, KernelError> {
        self.base.enchantment_should_start_at_bottom(id)
    }
    fn enchantment_modify_shuffle_order(
        &self,
        state: &BattleState,
        card: CardHandle,
        cards: &mut Vec<CardHandle>,
        is_initial_shuffle: bool,
    ) -> Result<(), KernelError> {
        self.base
            .enchantment_modify_shuffle_order(state, card, cards, is_initial_shuffle)
    }
    fn listener_hook_mask(
        &self,
        state: &BattleState,
        listener: HookListener,
    ) -> Result<HookMask, KernelError> {
        self.base.listener_hook_mask(state, listener)
    }
    fn dispatch_hook(
        &self,
        state: &mut BattleState,
        listener: HookListener,
        slot: HookSlot,
        args: &HookArgs<'_>,
    ) -> Result<HookResult, KernelError> {
        self.base.dispatch_hook(state, listener, slot, args)
    }
}

#[test]
fn stunned_monster_executes_without_perform_move_and_semantic_history_round_trips() {
    let content = FailOnMoveContent::default();
    let (mut state, enemy) = make_test_battle(None, 7);
    CombatEngine::start_combat(&mut state, &content.base).unwrap();

    // Record card_block_gains and verify snapshot export/import restores all semantic history fields
    state.record_block_gain(ValueProp::MOVE, Some(1));
    state.record_energy_spent(2);
    state.record_orb_channel(OrbId::FrostOrb);
    let json = export_live_state(&state).unwrap();
    assert_eq!(json.semantic_history.card_block_gains.len(), 1);
    let restored = import_live_state(&json, &content).unwrap();
    assert_eq!(restored.semantic_history.card_block_gains.len(), 1);
    assert_eq!(restored.semantic_history.card_block_gains[0].2, 1);
    assert_eq!(restored.semantic_history.energy_spent.len(), 1);
    assert_eq!(restored.semantic_history.orb_channels.len(), 1);
    assert_eq!(restored.semantic_history.card_drawn.len(), 5);

    // Stun the enemy with a concrete follow-up state, end turn, and confirm STUNNED move executes via
    // monster_perform_move while monster_roll_move is NOT called for Turn 2 follow-up restoration!
    creature::stun(&mut state, enemy, Some("ACID_GOOP")).unwrap();
    let outcome = CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert_eq!(outcome, StepOutcome::Completed);
    let m = state.creature(enemy).unwrap().monster.as_ref().unwrap();
    assert!(m.performed_first_move);
    assert_eq!(m.current_state_id, "ACID_GOOP");
    assert_eq!(m.next_move_id.as_deref(), Some("ACID_GOOP"));
    assert!(m.transient_move_follow_up_state_id.is_none());
}

#[test]
fn wave2_wave3_kernel_additions_work() {
    let content = PartBTestContent::default();
    content.enable_wave2_hooks.set(true);
    let (mut state, enemy) = make_test_battle(None, 7);
    let mut ctx = (*state.static_ctx).clone();
    ctx.total_floor = 17;
    state.static_ctx = Arc::new(ctx);

    CombatEngine::start_combat(&mut state, &content).unwrap();
    let pc = state.player.creature_handle;

    // 1. HookSlot::ModifyXValue & HookSlot::AfterPlayerTurnStartLate & total_floor snapshot round-trip
    let h0 = state.player.hand[0];
    let x_val = sts2_kernel::hooks::modify_x_value(&mut state, &content, h0, 3).unwrap();
    assert_eq!(x_val, 5);
    assert!(content.events.borrow().contains(&"after_turn_start_late".to_owned()));
    let snap = export_live_state(&state).unwrap();
    assert_eq!(snap.total_floor, 17);
    let restored = import_live_state(&snap, &content).unwrap();
    assert_eq!(restored.static_ctx.total_floor, 17);

    // 2. AfterBlockBroken with breaker + ModifyDamageHookType::CAP skipping Additive(+100) while applying Cap(7)
    creature::gain_block_internal(&mut state, enemy, Dec::from_i32(5)).unwrap();
    let res_cap = damage::damage_targets_with_hook_mode(
        &mut state,
        &content,
        &[enemy],
        Dec::from_i32(10),
        ValueProp::MOVE,
        Some(pc),
        None,
        None,
        ModifyDamageHookType::CAP,
    )
    .unwrap();
    assert_eq!(res_cap[0].blocked_damage, 5);
    assert_eq!(res_cap[0].unblocked_damage, 2); // capped at 7 -> 5 blocked + 2 unblocked
    assert!(content
        .events
        .borrow()
        .contains(&format!("block_broken:t={}:b=Some({})", enemy.0, pc.0)));

    // 3. AutoPlay from_top_of_draw_pile_with_upgrade + move_to_result_pile_without_playing
    let mut choices = SmallVec::new();
    autoplay::from_top_of_draw_pile_with_upgrade(&mut state, &content, 1, false, true, &mut choices).unwrap();
    let played_top = *state.player.discard.last().unwrap();
    assert_eq!(state.card(played_top).unwrap().upgrade_level, 1);

    let unplayed = card_pile::add_generated_card_to_combat(
        &mut state,
        &content,
        CardId::StrikeIronclad,
        PileType::Play,
        CardPilePosition::Bottom,
        0,
        None,
    )
    .unwrap();
    card_pile::move_to_result_pile_without_playing(&mut state, &content, unplayed).unwrap();
    assert!(state.player.discard.contains(&unplayed));

    // 4. card_resolved_type / card_resolved_target_type override + card_thrash_damage_variable + transform_with_upgrade
    let mad = card_pile::add_generated_card_to_combat(
        &mut state,
        &content,
        CardId::MadScience,
        PileType::Hand,
        CardPilePosition::Bottom,
        0,
        Some(pc),
    )
    .unwrap();
    assert_eq!(content.card_thrash_damage_variable(&state, mad).unwrap(), Dec::from_i32(12));
    CombatEngine::play_card(&mut state, &content, mad, None, &mut choices).unwrap();
    assert!(state.card(mad).unwrap().removed_from_state);

    let to_trans = state.player.hand[0];
    let rock = card::transform_with_upgrade(&mut state, &content, to_trans, CardId::GiantRock, 1)
        .unwrap()
        .unwrap();
    assert_eq!(state.card(rock).unwrap().id, CardId::GiantRock);
    assert_eq!(state.card(rock).unwrap().upgrade_level, 1);

    // 5. TurnEndInHand ordering: Burn in hand moves to Play -> card_on_turn_end_in_hand -> Discard
    let _burn = card_pile::add_generated_card_to_combat(
        &mut state,
        &content,
        CardId::Burn,
        PileType::Hand,
        CardPilePosition::Bottom,
        0,
        None,
    )
    .unwrap();
    CombatEngine::end_player_turn(&mut state, &content).unwrap();
    assert!(content
        .events
        .borrow()
        .contains(&"turn_end_in_hand:Burn".to_owned()));

    // 6. Addendum: card_default_result_location + transform_into_after_play (stops replay after 1 play and returns transformed card to Hand) + ArrowCmd::shoot_arrow_at_target_with_action
    let mb = card_pile::add_generated_card_to_combat(
        &mut state,
        &content,
        CardId::MourningBlow,
        PileType::Hand,
        CardPilePosition::Bottom,
        0,
        None,
    )
    .unwrap();
    if let Some(c) = state.card_mut(mb) {
        c.base_replay_count = 1; // would play twice unless transform_into_after_play stops loop!
    }
    let hp_before_mb = state.creature(enemy).unwrap().current_hp;
    CombatEngine::play_card(&mut state, &content, mb, Some(enemy), &mut choices).unwrap();
    assert_eq!(state.creature(enemy).unwrap().current_hp, hp_before_mb - 7); // played once (capped at 7 by ModifyDamageCap hook), not twice (14)
    assert!(state.card(mb).unwrap().removed_from_state);
    assert!(state
        .player
        .hand
        .iter()
        .any(|&h| state.card(h).is_some_and(|c| c.id == CardId::Sadness)));

    sts2_kernel::commands::arrow::gain_arrow_slot(&mut state);
    let mut action_ran = false;
    let shot_ok = sts2_kernel::commands::arrow::shoot_arrow_at_target_with_action(
        &mut state,
        &content,
        enemy,
        Some(h0),
        |s, _c, arrow_idx| {
            action_ran = true;
            assert!(arrow_idx < s.player.arrows.len());
            Ok(())
        },
    )
    .unwrap();
    assert!(shot_ok && action_ran);
    assert!(state
        .creature(enemy)
        .unwrap()
        .powers
        .iter()
        .any(|p| p.id == PowerId::ImpaledPower));

    // 7. Addendum 2: temporary Sly/Retain recognized by card_pile::card_has_keyword + discard_batch triggers Sly autoplay
    // Also verify empty local_keywords on upgraded cards (e.g. Mirage+) is preserved across snapshot round-trip.
    let sly_card = card_pile::add_generated_card_to_combat(
        &mut state,
        &content,
        CardId::Mirage,
        PileType::Hand,
        CardPilePosition::Bottom,
        0,
        None,
    )
    .unwrap();
    if let Some(c) = state.card_mut(sly_card) {
        c.upgrade_level = 1;
        c.local_keywords = CardKeywords::empty();
    }
    let mirage_snap = export_live_state(&state).unwrap();
    let mirage_restored = import_live_state(&mirage_snap, &content).unwrap();
    assert_eq!(
        mirage_restored.card(sly_card).unwrap().local_keywords,
        CardKeywords::empty()
    );

    let retain_card = card_pile::add_generated_card_to_combat(
        &mut state,
        &content,
        CardId::StrikeIronclad,
        PileType::Hand,
        CardPilePosition::Bottom,
        0,
        None,
    )
    .unwrap();
    card::apply_single_turn_retain(&mut state, retain_card);
    assert!(card_pile::card_has_keyword(&mut state, &content, retain_card, CardKeywords::RETAIN).unwrap());

    card::apply_single_turn_sly(&mut state, sly_card);
    assert!(card_pile::card_has_keyword(&mut state, &content, sly_card, CardKeywords::SLY).unwrap());
    let played_before = state.player.counters.cards_played_this_combat;
    card::discard_batch(&mut state, &content, &[sly_card]).unwrap();
    assert_eq!(state.player.counters.cards_played_this_combat, played_before + 1);
}

#[test]
fn part_a_kernel_additions_verify() {
    use sts2_kernel::commands::{autoplay, block, card_select, orb, power};
    use sts2_kernel::HookListener;
    use sts2_types::CardTags;

    assert_eq!(CardTags::MINION.bits(), 1 << 8);

    let content = PartBTestContent::default();
    content.enable_wave2_hooks.set(true);
    let (mut state, enemy) = make_test_battle(None, 7);
    state.player.relics.push(sts2_kernel::RelicInst {
        id: RelicId::WhisperingEarring,
        is_wax: false,
        is_melted: false,
        stack_count: 1,
        floor_added_to_deck: None,
        private_fields: BTreeMap::new(),
    });
    CombatEngine::start_combat(&mut state, &content).unwrap();

    // 1. AfterAutoPrePlayPhaseEntered dispatches before AfterAutoPrePlayPhaseEnteredLate
    let events = content.events.borrow();
    let pre_pos = events.iter().position(|e| e == "after_auto_pre_play").unwrap();
    let late_pos = events.iter().position(|e| e == "after_auto_pre_play_late").unwrap();
    assert!(pre_pos < late_pos);
    drop(events);

    // 2. Vakuu auto-select takes candidates[..max_count] synchronously without PendingChoice
    let cands: Vec<CardHandle> = state.player.hand.iter().copied().collect();
    assert!(cands.len() >= 3);
    let selected = state
        .with_vakuu_auto_select(|s| {
            let mut empty_queue = SmallVec::new();
            match card_select::select_cards(
                s,
                &cands,
                1,
                2,
                ChoiceTiming::Action,
                "TEST.VAKUU",
                PileType::Hand,
                false,
                &mut empty_queue,
            )? {
                card_select::SelectResult::Selected(sel) => Ok(sel),
                card_select::SelectResult::NeedsChoice(_) => panic!("Vakuu must not pause"),
            }
        })
        .unwrap();
    assert_eq!(selected.as_slice(), &cands[..2]);
    assert!(!state.is_vakuu_auto_select_active());

    // 3. prepay_and_autoplay_card spends energy in hand and plays with captured X value
    let x_card = card_pile::add_generated_card_to_combat(
        &mut state,
        &content,
        CardId::Whirlwind,
        PileType::Hand,
        CardPilePosition::Bottom,
        0,
        None,
    )
    .unwrap();
    state.player.energy = 2;
    let outcome = autoplay::prepay_and_autoplay_card(&mut state, &content, x_card, Some(enemy)).unwrap();
    assert_eq!(outcome, StepOutcome::Completed);
    assert_eq!(state.player.energy, 0);
    assert_eq!(state.card(x_card).unwrap().captured_energy_x_value, 2);

    // 4. AfterModifyingBlockAmount iterates in #1082 order (Power before Relic) with ModifyBlock payload
    let pc = state.player.creature_handle;
    power::apply(&mut state, &content, PowerId::VulnerablePower, pc, Dec::ONE, Some(pc), None)
        .unwrap();
    content.events.borrow_mut().clear();
    let gained = block::gain_block(&mut state, &content, pc, Dec::from_i32(5), ValueProp::MOVE, None, None)
        .unwrap();
    assert_eq!(gained, Dec::from_i32(14)); // (5 + 2) * 2
    let mod_events: Vec<String> = content
        .events
        .borrow()
        .iter()
        .filter(|e| e.starts_with("after_mod_block:"))
        .cloned()
        .collect();
    assert_eq!(mod_events.len(), 2);
    assert!(mod_events[0].contains("Power"));
    assert!(mod_events[1].contains(&format!("{:?}", HookListener::Relic(0))));
    assert!(mod_events[0].ends_with(":t=1:amt=14"));

    // 5. ModifyOrbPassiveTriggerCount passes orb_idx so duplicate orb types only boost orb_idx == 0
    state.player.orb_capacity = 3;
    orb::channel(&mut state, &content, OrbId::LightningOrb).unwrap();
    orb::channel(&mut state, &content, OrbId::LightningOrb).unwrap();
    let (cnt0, mods0) =
        sts2_kernel::hooks::modify_orb_passive_trigger_count(&mut state, &content, OrbId::LightningOrb, 0, 1)
            .unwrap();
    let (cnt1, mods1) =
        sts2_kernel::hooks::modify_orb_passive_trigger_count(&mut state, &content, OrbId::LightningOrb, 1, 1)
            .unwrap();
    assert_eq!(cnt0, 2);
    assert_eq!(mods0.len(), 1);
    assert_eq!(cnt1, 1);
    assert!(mods1.is_empty());

    // 6. is_eligible_hook_listener checks enchantment/affliction presence on Card(h)
    let ench_card = state.player.hand[0];
    state.card_ext_mut(ench_card).unwrap().enchantment = Some(sts2_kernel::EnchantmentInst {
        id: sts2_types::EnchantmentId::Vigorous,
        amount: 4,
        status: sts2_types::EnchantmentStatus::Normal,
        private_fields: BTreeMap::new(),
    });
    card::afflict(&mut state, ench_card, sts2_types::AfflictionId::Bound, 1);
    assert!(sts2_kernel::hooks::is_eligible_hook_listener(
        &state,
        HookListener::Card(ench_card)
    ));
    assert!(sts2_kernel::hooks::is_eligible_hook_listener(
        &state,
        HookListener::Enchantment(ench_card)
    ));
    assert!(sts2_kernel::hooks::is_eligible_hook_listener(
        &state,
        HookListener::Affliction(ench_card)
    ));
    card::clear_affliction(&mut state, ench_card);
    state.card_ext_mut(ench_card).unwrap().enchantment = None;
    assert!(sts2_kernel::hooks::is_eligible_hook_listener(
        &state,
        HookListener::Card(ench_card)
    ));
    assert!(!sts2_kernel::hooks::is_eligible_hook_listener(
        &state,
        HookListener::Enchantment(ench_card)
    ));
    assert!(!sts2_kernel::hooks::is_eligible_hook_listener(
        &state,
        HookListener::Affliction(ench_card)
    ));
}
