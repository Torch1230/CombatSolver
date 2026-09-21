"""Cheap state features, in fixed units. No card/encounter IDs or terminal labels.

Units are normalization constants, not utility weights. C# parity is required before
any exported model is admitted to live search. Coefficients start at zero correction.
"""
NAMES = (
    'energy', 'hp_pressure', 'incoming_fraction', 'enemy_hp', 'hand', 'free_plays',
    'reachable_hand', 'persistent', 'setup', 'retained_attack', 'replay', 'future_resource',
    'delayed_damage', 'clutter_fraction', 'strength_suppression', 'weak_turns', 'vulnerable_turns',
    'deck_size', 'turn', 'energy_x_pressure', 'energy_x_incoming', 'hand_x_energy',
    'persistent_x_pressure', 'setup_x_pressure', 'future_x_pressure',
    'delayed_x_enemy_hp', 'reachable_x_incoming', 'free_x_incoming',
)


def capture(observation):
    e = observation['retention']['evaluation']
    cap = lambda value, scale: max(0.0, min(4.0, value / scale))
    energy = cap(e['energy'], 6)
    pressure = 1.0 - max(0.0, min(1.0, observation['playerHp'] / max(1, observation['playerMaxHp'])))
    incoming = max(0.0, min(1.0, (observation['playerHp'] - e['projectedPlayerHp']) / max(1, observation['playerHp'])))
    enemy_hp = cap(observation['enemyHp'], 200)
    hand = cap(e['handCount'], 10)
    free = cap(e['zeroCostPlayableCount'], 10)
    reachable = cap(e['reachableHandValue'], 50)
    persistent = cap(e['persistentBuffValue'], 128)
    setup = cap(e['latentSetupValue'], 64)
    future = cap(e['futureResourceValue'], 64)
    delayed = cap(e['delayedDamageValue'], 100)
    return [energy, pressure, incoming, enemy_hp, hand, free, reachable, persistent, setup,
            cap(e['retainedAttackValue'], 128), cap(e['replayPotentialValue'], 64), future, delayed,
            max(0.0, min(1.0, e['liveDeckClutter'] / max(1, e['liveDeckSize']))),
            cap(e['enemyStrengthSuppression'], 16), cap(e['enemyWeakTurns'], 16),
            cap(e['enemyVulnerableTurns'], 16), cap(e['liveDeckSize'], 30), cap(observation['turn'], 10),
            energy * pressure, energy * incoming, hand * energy, persistent * pressure,
            setup * pressure, future * pressure, delayed * enemy_hp, reachable * incoming, free * incoming]
