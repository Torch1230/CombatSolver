use crate::content_dispatch::ContentDispatcher;
use crate::error::KernelError;
use crate::hooks;
use crate::state::{BattleState, CardHandle, CardInst};
use core::cmp::Ordering;
use smallvec::SmallVec;
use std::sync::LazyLock;
use sts2_types::{AfflictionId, CardId, CardStaticFlags, Dec, EnchantmentId, HookSlot, Rng};

#[inline(always)]
pub fn resolve_local_energy_cost_card(state: &BattleState, card: &CardInst, is_x_energy: bool) -> i32 {
    if card.canonical_base_energy_cost < 0 || is_x_energy {
        return card.canonical_base_energy_cost as i32;
    }
    let mut cost = (card.base_energy_cost as i32).max(0);
    if let Some(ext) = state.card_ext(card.ext_index) {
        if ext.local_energy_modifiers.is_empty() {
            return cost;
        }
        for modifier in &ext.local_energy_modifiers {
            let next = if modifier.kind.is_absolute() {
                modifier.amount
            } else {
                cost + modifier.amount
            };
            cost = if modifier.is_reduce_only {
                cost.min(next)
            } else {
                next
            };
        }
    }
    cost.max(0)
}

#[inline]
pub fn resolve_local_energy_cost(state: &BattleState, card_handle: CardHandle, is_x_energy: bool) -> i32 {
    let card = state.card(card_handle).expect("card handle exists");
    resolve_local_energy_cost_card(state, card, is_x_energy)
}

pub fn resolve_energy_cost(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
) -> Result<i32, KernelError> {
    let card = state
        .card(card_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card handle".to_owned()))?;
    let meta = state.fast_card_meta(content, card.id)?;
    let is_x = meta
        .flags
        .contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST);
    let local_cost = resolve_local_energy_cost_card(state, card, is_x);
    if is_x {
        return Ok(local_cost);
    }
    let (modified, was_modified) =
        hooks::modify_energy_cost_in_combat(state, content, card_handle, Dec::from_i32(local_cost))?;
    if was_modified {
        Ok(modified.max(Dec::ZERO).to_i32_trunc()?)
    } else {
        Ok(local_cost)
    }
}

#[inline]
pub fn resolve_local_star_cost_card(card: &CardInst, canonical_star_cost: i32) -> i32 {
    let local = if let Some(override_turn) = card.temp_star_cost_override_this_turn.get() {
        override_turn
    } else if card.temp_flags.intersects(
        crate::state::CardTempFlags::TEMPORARY_FREE_TURN
            | crate::state::CardTempFlags::TEMPORARY_FREE_PLAY
            | crate::state::CardTempFlags::TEMPORARY_FREE_COMBAT,
    ) {
        0
    } else {
        return canonical_star_cost;
    };
    if local == 0 && canonical_star_cost < 0 {
        canonical_star_cost
    } else {
        local
    }
}

#[inline]
pub fn resolve_local_star_cost(state: &BattleState, card_handle: CardHandle, canonical_star_cost: i32) -> i32 {
    let card = state.card(card_handle).expect("card handle exists");
    resolve_local_star_cost_card(card, canonical_star_cost)
}

pub fn resolve_star_cost(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
) -> Result<i32, KernelError> {
    let card = state
        .card(card_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card handle".to_owned()))?;
    let meta = state.fast_card_meta(content, card.id)?;
    if meta
        .flags
        .contains(sts2_types::CardStaticFlags::IS_X_STAR_COST)
    {
        return Ok(state.player.stars);
    }
    let local_cost = resolve_local_star_cost_card(card, meta.canonical_star_cost);
    let (modified, was_modified) =
        hooks::modify_star_cost_in_combat(state, content, card_handle, Dec::from_i32(local_cost))?;
    if was_modified {
        Ok(modified.max(Dec::ZERO).to_i32_trunc()?)
    } else {
        Ok(local_cost)
    }
}

pub fn resolved_energy_cost_for_comparison(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    card_handle: CardHandle,
) -> Result<i32, KernelError> {
    let card = state
        .card(card_handle)
        .ok_or_else(|| KernelError::InvalidState("missing card handle".to_owned()))?;
    let meta = state.fast_card_meta(content, card.id)?;
    if meta
        .flags
        .contains(sts2_types::CardStaticFlags::IS_X_ENERGY_COST)
    {
        return Ok(card.captured_energy_x_value as i32);
    }
    if card.canonical_base_energy_cost < 0 {
        return Ok(0);
    }
    Ok(resolve_energy_cost(state, content, card_handle)?.max(0))
}

/// Exact port of `CardModel.CompareTo(AbstractModel? other)` (v0.113.1).
pub fn compare_cards(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    a: CardHandle,
    b: CardHandle,
) -> Result<Ordering, KernelError> {
    if a == b {
        return Ok(Ordering::Equal);
    }
    let (ca, cb) = {
        let ca = state
            .card(a)
            .ok_or_else(|| KernelError::InvalidState("missing card a".to_owned()))?;
        let cb = state
            .card(b)
            .ok_or_else(|| KernelError::InvalidState("missing card b".to_owned()))?;
        (*ca, *cb)
    };

    let by_id = ca.id.compare_model_id(cb.id);
    if by_id != Ordering::Equal {
        return Ok(by_id);
    }

    let by_upgrade = ca.upgrade_level.cmp(&cb.upgrade_level);
    if by_upgrade != Ordering::Equal {
        return Ok(by_upgrade);
    }

    let ench_a = state
        .card_ext(ca.ext_index)
        .and_then(|e| e.enchantment.as_ref().map(|x| x.id));
    let ench_b = state
        .card_ext(cb.ext_index)
        .and_then(|e| e.enchantment.as_ref().map(|x| x.id));
    let by_ench = match (ench_a, ench_b) {
        (None, None) => Ordering::Equal,
        (None, Some(_)) => Ordering::Less,
        (Some(_), None) => Ordering::Greater,
        (Some(x), Some(y)) => x.compare_model_id(y),
    };
    if by_ench != Ordering::Equal {
        return Ok(by_ench);
    }

    let aff_a = ca.affliction.map(|x| x.id);
    let aff_b = cb.affliction.map(|x| x.id);
    let by_aff = match (aff_a, aff_b) {
        (None, None) => Ordering::Equal,
        (None, Some(_)) => Ordering::Less,
        (Some(_), None) => Ordering::Greater,
        (Some(x), Some(y)) => x.compare_model_id(y),
    };
    if by_aff != Ordering::Equal {
        return Ok(by_aff);
    }

    let cost_a = resolved_energy_cost_for_comparison(state, content, a)?;
    let cost_b = resolved_energy_cost_for_comparison(state, content, b)?;
    let by_cost = cost_a.cmp(&cost_b);
    if by_cost != Ordering::Equal {
        return Ok(by_cost);
    }

    let by_replay = ca.base_replay_count.cmp(&cb.base_replay_count);
    if by_replay != Ordering::Equal {
        return Ok(by_replay);
    }

    let vars_a = content.card_comparison_base_values(state, a)?;
    let vars_b = content.card_comparison_base_values(state, b)?;
    for ((_, va), (_, vb)) in vars_a.iter().zip(vars_b.iter()) {
        let ca_val = (*va).max(Dec::ZERO);
        let cb_val = (*vb).max(Dec::ZERO);
        let cmp = ca_val.cmp(&cb_val);
        if cmp != Ordering::Equal {
            return Ok(cmp);
        }
    }
    Ok(Ordering::Equal)
}

fn build_rank_table<const N: usize>(mut cmp_idx: impl FnMut(usize, usize) -> Ordering) -> [u16; N] {
    let mut order: Vec<u16> = (0..N as u16).collect();
    order.sort_unstable_by(|&a, &b| cmp_idx(a as usize, b as usize));
    let mut ranks = [0u16; N];
    for (rank, &raw_id) in order.iter().enumerate() {
        ranks[raw_id as usize] = rank as u16;
    }
    ranks
}

static CARD_MODEL_ORDER: LazyLock<[u16; CardId::COUNT]> = LazyLock::new(|| {
    build_rank_table(|a, b| {
        let ca: CardId = unsafe { core::mem::transmute(a as u16) };
        let cb: CardId = unsafe { core::mem::transmute(b as u16) };
        ca.compare_model_id(cb)
    })
});

static ENCH_MODEL_ORDER: LazyLock<[u16; EnchantmentId::COUNT]> = LazyLock::new(|| {
    build_rank_table(|a, b| {
        let ea: EnchantmentId = unsafe { core::mem::transmute(a as u16) };
        let eb: EnchantmentId = unsafe { core::mem::transmute(b as u16) };
        ea.compare_model_id(eb)
    })
});

static AFF_MODEL_ORDER: LazyLock<[u16; AfflictionId::COUNT]> = LazyLock::new(|| {
    build_rank_table(|a, b| {
        let aa: AfflictionId = unsafe { core::mem::transmute(a as u16) };
        let ab: AfflictionId = unsafe { core::mem::transmute(b as u16) };
        aa.compare_model_id(ab)
    })
});

#[inline(always)]
pub const fn card_has_dynamic_comparison_values(id: CardId) -> bool {
    matches!(
        id,
        CardId::Disintegration
            | CardId::Dowsing
            | CardId::Guilty
            | CardId::Maul
            | CardId::SovereignBlade
            | CardId::TheBall
            | CardId::Wither
            | CardId::Claw
            | CardId::GeneticAlgorithm
            | CardId::ShowOff
            | CardId::Rampage
            | CardId::Thrash
            | CardId::TheScythe
            | CardId::KinglyPunch
    )
}

#[derive(Clone, Copy)]
struct ShuffleCardKey {
    handle: CardHandle,
    card_id: CardId,
    card_rank: u16,
    upgrade_level: i32,
    ench_rank: u16,
    aff_rank: u16,
    base_replay_count: i32,
    orig_idx: usize,
}

#[inline]
fn local_cmp_cost(state: &BattleState, card: &CardInst, is_x_energy: bool) -> i32 {
    if is_x_energy {
        card.captured_energy_x_value as i32
    } else if card.canonical_base_energy_cost < 0 {
        0
    } else {
        resolve_local_energy_cost_card(state, card, false).max(0)
    }
}

#[inline(always)]
fn dotnet_swap_if_greater<T>(
    keys: &mut [T],
    cmp: &mut impl FnMut(&T, &T) -> Ordering,
    i: usize,
    j: usize,
) {
    if cmp(&keys[i], &keys[j]) == Ordering::Greater {
        keys.swap(i, j);
    }
}

fn dotnet_insertion_sort<T: Copy>(keys: &mut [T], cmp: &mut impl FnMut(&T, &T) -> Ordering) {
    if keys.len() <= 1 {
        return;
    }
    for i in 0..keys.len() - 1 {
        let t = keys[i + 1];
        let mut j = i as isize;
        while j >= 0 && cmp(&t, &keys[j as usize]) == Ordering::Less {
            keys[(j + 1) as usize] = keys[j as usize];
            j -= 1;
        }
        keys[(j + 1) as usize] = t;
    }
}

fn dotnet_down_heap<T: Copy>(
    keys: &mut [T],
    mut i: usize,
    n: usize,
    cmp: &mut impl FnMut(&T, &T) -> Ordering,
) {
    let d = keys[i - 1];
    while i <= (n >> 1) {
        let mut child = 2 * i;
        if child < n && cmp(&keys[child - 1], &keys[child]) == Ordering::Less {
            child += 1;
        }
        if cmp(&d, &keys[child - 1]) != Ordering::Less {
            break;
        }
        keys[i - 1] = keys[child - 1];
        i = child;
    }
    keys[i - 1] = d;
}

fn dotnet_heap_sort<T: Copy>(keys: &mut [T], cmp: &mut impl FnMut(&T, &T) -> Ordering) {
    let n = keys.len();
    for i in (1..=(n >> 1)).rev() {
        dotnet_down_heap(keys, i, n, cmp);
    }
    for i in (2..=n).rev() {
        keys.swap(0, i - 1);
        dotnet_down_heap(keys, 1, i - 1, cmp);
    }
}

fn dotnet_pick_pivot_and_partition<T: Copy>(
    keys: &mut [T],
    cmp: &mut impl FnMut(&T, &T) -> Ordering,
) -> usize {
    let hi = keys.len() - 1;
    let mid = hi >> 1;
    dotnet_swap_if_greater(keys, cmp, 0, mid);
    dotnet_swap_if_greater(keys, cmp, 0, hi);
    dotnet_swap_if_greater(keys, cmp, mid, hi);
    let pivot = keys[mid];
    keys.swap(mid, hi - 1);
    let mut left = 0usize;
    let mut right = hi - 1;
    while left < right {
        loop {
            left += 1;
            if cmp(&keys[left], &pivot) != Ordering::Less {
                break;
            }
        }
        loop {
            right -= 1;
            if cmp(&pivot, &keys[right]) != Ordering::Less {
                break;
            }
        }
        if left >= right {
            break;
        }
        keys.swap(left, right);
    }
    if left != hi - 1 {
        keys.swap(left, hi - 1);
    }
    left
}

fn dotnet_intro_sort<T: Copy>(
    mut keys: &mut [T],
    mut depth_limit: usize,
    cmp: &mut impl FnMut(&T, &T) -> Ordering,
) {
    let mut partition_size = keys.len();
    while partition_size > 1 {
        if partition_size <= 16 {
            match partition_size {
                2 => dotnet_swap_if_greater(keys, cmp, 0, 1),
                3 => {
                    dotnet_swap_if_greater(keys, cmp, 0, 1);
                    dotnet_swap_if_greater(keys, cmp, 0, 2);
                    dotnet_swap_if_greater(keys, cmp, 1, 2);
                }
                _ => dotnet_insertion_sort(&mut keys[..partition_size], cmp),
            }
            break;
        }
        if depth_limit == 0 {
            dotnet_heap_sort(&mut keys[..partition_size], cmp);
            break;
        }
        depth_limit -= 1;
        let p = dotnet_pick_pivot_and_partition(&mut keys[..partition_size], cmp);
        dotnet_intro_sort(&mut keys[p + 1..partition_size], depth_limit, cmp);
        keys = &mut keys[..p];
        partition_size = p;
    }
}

/// Exact port of .NET 9 `ArraySortHelper<T>.IntrospectiveSort`.
#[inline]
pub fn dotnet_introspective_sort<T: Copy>(
    keys: &mut [T],
    cmp: &mut impl FnMut(&T, &T) -> Ordering,
) {
    if keys.len() > 1 {
        let depth_limit = 2 * ((keys.len() as u32).ilog2() as usize + 1);
        dotnet_intro_sort(keys, depth_limit, cmp);
    }
}

#[inline]
fn compare_shuffle_tiebreak(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    a: &ShuffleCardKey,
    b: &ShuffleCardKey,
    cost_cache: &mut [i32],
    has_dyn_vars: bool,
) -> Result<Ordering, KernelError> {
    if a.handle == b.handle {
        return Ok(Ordering::Equal);
    }
    let mut cost_a = cost_cache[a.orig_idx];
    if cost_a == i32::MIN {
        cost_a = resolved_energy_cost_for_comparison(state, content, a.handle)?;
        cost_cache[a.orig_idx] = cost_a;
    }
    let mut cost_b = cost_cache[b.orig_idx];
    if cost_b == i32::MIN {
        cost_b = resolved_energy_cost_for_comparison(state, content, b.handle)?;
        cost_cache[b.orig_idx] = cost_b;
    }
    let by_cost = cost_a.cmp(&cost_b);
    if by_cost != Ordering::Equal {
        return Ok(by_cost);
    }

    let by_replay = a.base_replay_count.cmp(&b.base_replay_count);
    if by_replay != Ordering::Equal {
        return Ok(by_replay);
    }
    if !has_dyn_vars {
        return Ok(Ordering::Equal);
    }

    let vars_a = content.card_comparison_base_values(state, a.handle)?;
    let vars_b = content.card_comparison_base_values(state, b.handle)?;
    for ((_, va), (_, vb)) in vars_a.iter().zip(vars_b.iter()) {
        let ca_val = (*va).max(Dec::ZERO);
        let cb_val = (*vb).max(Dec::ZERO);
        let cmp = ca_val.cmp(&cb_val);
        if cmp != Ordering::Equal {
            return Ok(cmp);
        }
    }
    Ok(Ordering::Equal)
}

fn stable_sort_shuffle_cards_generic(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    handles: &mut [CardHandle],
) -> Result<(), KernelError> {
    let n = handles.len();
    let card_ranks = &*CARD_MODEL_ORDER;
    let ench_ranks = &*ENCH_MODEL_ORDER;
    let aff_ranks = &*AFF_MODEL_ORDER;
    let mut keys: SmallVec<[ShuffleCardKey; 128]> = SmallVec::with_capacity(n);
    for (orig_idx, &h) in handles.iter().enumerate() {
        let c = state
            .card(h)
            .ok_or_else(|| KernelError::InvalidState("missing card in shuffle".to_owned()))?;
        let ench_rank = state
            .card_ext(c.ext_index)
            .and_then(|e| e.enchantment.as_ref().map(|x| ench_ranks[x.id as usize] + 1))
            .unwrap_or(0);
        let aff_rank = c
            .affliction
            .map_or(0, |x| aff_ranks[x.id as usize] + 1);
        keys.push(ShuffleCardKey {
            handle: h,
            card_id: c.id,
            card_rank: card_ranks[c.id as usize],
            upgrade_level: c.upgrade_level,
            ench_rank,
            aff_rank,
            base_replay_count: c.base_replay_count as i32,
            orig_idx,
        });
    }
    let mut cost_cache: SmallVec<[i32; 256]> = SmallVec::new();
    let mut err: Option<KernelError> = None;
    dotnet_introspective_sort(&mut keys, &mut |a, b| {
        if err.is_some() || a.handle == b.handle {
            return Ordering::Equal;
        }
        let cmp = a
            .card_rank
            .cmp(&b.card_rank)
            .then_with(|| a.upgrade_level.cmp(&b.upgrade_level))
            .then_with(|| a.ench_rank.cmp(&b.ench_rank))
            .then_with(|| a.aff_rank.cmp(&b.aff_rank));
        if cmp != Ordering::Equal {
            return cmp;
        }
        if cost_cache.is_empty() {
            cost_cache.resize(n, i32::MIN);
        }
        let has_dyn_vars = card_has_dynamic_comparison_values(a.card_id);
        match compare_shuffle_tiebreak(state, content, a, b, &mut cost_cache, has_dyn_vars) {
            Ok(ord) => ord,
            Err(e) => {
                err = Some(e);
                Ordering::Equal
            }
        }
    });
    if let Some(e) = err {
        return Err(e);
    }
    for (dst, k) in handles.iter_mut().zip(keys.iter()) {
        *dst = k.handle;
    }
    Ok(())
}

/// Exact port of `ListExtensions.StableShuffle`: sort by `CardModel.CompareTo`, then `UnstableShuffle`.
pub fn stable_shuffle_cards(
    state: &mut BattleState,
    content: &dyn ContentDispatcher,
    handles: &mut [CardHandle],
    rng: &mut Rng,
) -> Result<(), KernelError> {
    if handles.len() <= 1 {
        rng.unstable_shuffle(handles);
        return Ok(());
    }
    let can_try_fast = handles.len() <= u16::MAX as usize
        && state.hook_cache.dirty_mask == 0
        && state.hook_cache.cached_tables.is_some()
        && !state
            .hook_cache
            .masks
            .combat_combined
            .contains(HookSlot::TryModifyEnergyCostInCombat)
        && !state
            .hook_cache
            .masks
            .combat_combined
            .contains(HookSlot::TryModifyEnergyCostInCombatLate)
        && CardId::COUNT <= 1024
        && EnchantmentId::COUNT < 255
        && AfflictionId::COUNT < 255;

    let mut did_fast = false;
    if can_try_fast {
        #[cfg(debug_assertions)]
        let mut dbg_orig: SmallVec<[CardHandle; 256]> = SmallVec::from_slice(handles);
        let tables = state.hook_cache.cached_tables.expect("checked above");
        let card_ranks = &*CARD_MODEL_ORDER;
        let ench_ranks = &*ENCH_MODEL_ORDER;
        let aff_ranks = &*AFF_MODEL_ORDER;
        let mut fast_keys: SmallVec<[(u64, CardHandle); 256]> =
            SmallVec::with_capacity(handles.len());
        let mut fast_ok = true;
        let hlen = handles.len();
        for (orig_idx, &h) in handles.iter().enumerate() {
            #[cfg(target_arch = "x86_64")]
            if orig_idx + 2 < hlen {
                let ahead = unsafe { *handles.get_unchecked(orig_idx + 2) }
                    .0
                    .wrapping_sub(1) as usize;
                if ahead < state.cards.len() {
                    unsafe {
                        core::arch::x86_64::_mm_prefetch::<{ core::arch::x86_64::_MM_HINT_T0 }>(
                            state.cards.as_ptr().add(ahead).cast::<i8>(),
                        );
                    }
                }
            }
            let c = state
                .card(h)
                .ok_or_else(|| KernelError::InvalidState("missing card in shuffle".to_owned()))?;
            if card_has_dynamic_comparison_values(c.id)
                || !(0..=63).contains(&c.upgrade_level)
                || !(0..=255).contains(&c.base_replay_count)
            {
                fast_ok = false;
                break;
            }
            let is_x = tables.card_metas[c.id as usize]
                .flags
                .contains(CardStaticFlags::IS_X_ENERGY_COST);
            let cost = local_cmp_cost(state, c, is_x);
            if !(0..=255).contains(&cost) {
                fast_ok = false;
                break;
            }
            let card_rank = card_ranks[c.id as usize];
            debug_assert!(card_rank < 1024);
            let ench_rank = state
                .card_ext(c.ext_index)
                .and_then(|e| e.enchantment.as_ref().map(|x| ench_ranks[x.id as usize] + 1))
                .unwrap_or(0);
            let aff_rank = c
                .affliction
                .map_or(0, |x| aff_ranks[x.id as usize] + 1);
            debug_assert!(ench_rank <= 255 && aff_rank <= 255);
            let key: u64 = ((card_rank as u64) << 54)
                | ((c.upgrade_level as u64) << 48)
                | ((ench_rank as u64) << 40)
                | ((aff_rank as u64) << 32)
                | ((cost as u64) << 24)
                | ((c.base_replay_count as u64) << 16);
            fast_keys.push((key, h));
        }
        if fast_ok {
            dotnet_introspective_sort(&mut fast_keys, &mut |a, b| a.0.cmp(&b.0));
            for (dst, &(_, h)) in handles.iter_mut().zip(fast_keys.iter()) {
                *dst = h;
            }
            #[cfg(debug_assertions)]
            {
                stable_sort_shuffle_cards_generic(state, content, &mut dbg_orig)?;
                debug_assert_eq!(handles, dbg_orig.as_slice());
            }
            did_fast = true;
        }
    }
    if !did_fast {
        stable_sort_shuffle_cards_generic(state, content, handles)?;
    }
    rng.unstable_shuffle(handles);
    Ok(())
}
