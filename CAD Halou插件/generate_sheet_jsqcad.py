#!/usr/bin/env python
"""Generate JSQCAD from sheet-angle models.

Two model branches are kept in this file:

1. ``profile`` (default)
   A 13-vertex outer profile built from the actual sheet-metal samples used in
   the angle videos. It supports 91 to 179 degrees through anchors at
   91 / 105 / 116 / 135 / 144 / 165 / 179.

2. ``legacy-full``
   The older 26-vertex seam-style profile. It is kept only for compatibility and
   still supports 105 to 144 degrees.
"""

from __future__ import annotations

import argparse
import bisect
import json
import math
import sys
from dataclasses import dataclass


Point = tuple[float, float]
DEFAULT_MODEL_NAME = "constructive-profile"


@dataclass(frozen=True)
class Sample:
    angle_deg: float
    vertices: tuple[Point, ...]
    closed: bool
    rotate_start: int = 0
    drop_last: bool = False

    def normalized_vertices(self) -> tuple[Point, ...]:
        pts = list(self.vertices)
        if self.drop_last:
            pts = pts[:-1]
        if self.rotate_start:
            pts = pts[self.rotate_start :] + pts[: self.rotate_start]
        return tuple(pts)


@dataclass(frozen=True)
class ModelSpec:
    key: str
    generator: str
    samples: tuple[Sample, ...]
    validate_simple: bool = True


LEGACY_FULL_SAMPLES: tuple[Sample, ...] = (
    Sample(
        angle_deg=91.0,
        closed=True,
        vertices=(
            (4069.2655, 1422.6954),
            (4078.0645, 1422.8489),
            (4077.9423, 1429.8479),
            (4090.6404, 1430.0695),
            (4090.7337, 1424.7232),
            (4087.6793, 1419.4328),
            (4087.6793, 1407.6251),
            (4090.6793, 1402.4289),
            (4090.6793, 1399.0176),
            (4064.4780, 1399.0176),
            (4063.9441, 1429.6035),
            (4069.9432, 1429.7083),
            (4070.0619, 1422.9093),
            (4069.2620, 1422.8953),
            (4069.1573, 1428.8944),
            (4064.7580, 1428.8176),
            (4065.2642, 1399.8176),
            (4089.8793, 1399.8176),
            (4089.8793, 1402.2146),
            (4086.8793, 1407.4107),
            (4086.8793, 1419.6471),
            (4089.9300, 1424.9310),
            (4089.8545, 1429.2557),
            (4078.7562, 1429.0620),
            (4078.8783, 1422.0630),
            (4069.2795, 1421.8955),
        ),
    ),
    Sample(
        angle_deg=105.0,
        closed=True,
        vertices=(
            (3906.8157, 1617.3219),
            (3915.3162, 1619.5996),
            (3913.5045, 1626.3610),
            (3925.7717, 1629.6480),
            (3927.1557, 1624.4831),
            (3924.1013, 1619.1927),
            (3924.1013, 1607.3850),
            (3927.1013, 1602.1888),
            (3927.1013, 1598.7775),
            (3906.4012, 1598.7775),
            (3899.9812, 1622.7375),
            (3905.7767, 1624.2904),
            (3907.5367, 1617.7221),
            (3906.7640, 1617.5150),
            (3905.2110, 1623.3106),
            (3900.9610, 1622.1718),
            (3907.0151, 1599.5775),
            (3926.3013, 1599.5775),
            (3926.3013, 1601.9745),
            (3923.3013, 1607.1706),
            (3923.3013, 1619.4070),
            (3926.2972, 1624.5961),
            (3925.2060, 1628.6682),
            (3914.4843, 1625.7954),
            (3916.2960, 1619.0339),
            (3907.0228, 1616.5491),
        ),
    ),
    Sample(
        angle_deg=116.0,
        closed=True,
        vertices=(
            (4111.3565, 1607.5617),
            (4119.2662, 1611.4195),
            (4116.1976, 1617.7111),
            (4127.6123, 1623.2784),
            (4129.9563, 1618.4724),
            (4126.9019, 1613.1820),
            (4126.9019, 1601.3743),
            (4129.9019, 1596.1782),
            (4129.9019, 1592.7668),
            (4112.7869, 1592.7668),
            (4103.6141, 1611.5738),
            (4109.0069, 1614.2040),
            (4111.9878, 1608.0922),
            (4111.2688, 1607.7415),
            (4108.6386, 1613.1343),
            (4104.6839, 1611.2054),
            (4113.2868, 1593.5668),
            (4129.1019, 1593.5668),
            (4129.1019, 1595.9638),
            (4126.1019, 1601.1600),
            (4126.1019, 1613.3964),
            (4129.0508, 1618.5040),
            (4127.2439, 1622.2087),
            (4117.2673, 1617.3428),
            (4120.3359, 1611.0512),
            (4111.7072, 1606.8427),
        ),
    ),
    Sample(
        angle_deg=135.0,
        closed=True,
        rotate_start=1,
        vertices=(
            (3627.3477, 1592.0742),
            (3626.7820, 1592.6398),
            (3633.0048, 1598.8626),
            (3628.0551, 1603.8124),
            (3637.0353, 1612.7926),
            (3640.8164, 1609.0116),
            (3637.7619, 1603.7212),
            (3637.7619, 1591.9135),
            (3640.7619, 1586.7174),
            (3640.7619, 1583.3060),
            (3628.7619, 1583.3060),
            (3618.1553, 1593.9126),
            (3622.3980, 1598.1553),
            (3627.2063, 1593.3470),
            (3626.6406, 1592.7813),
            (3622.3980, 1597.0239),
            (3619.2867, 1593.9126),
            (3629.0933, 1584.1060),
            (3639.9619, 1584.1060),
            (3639.9619, 1586.5030),
            (3636.9619, 1591.6992),
            (3636.9619, 1603.9356),
            (3639.8166, 1608.8800),
            (3637.0353, 1611.6613),
            (3629.1865, 1603.8124),
            (3634.1362, 1598.8626),
        ),
    ),
    Sample(
        angle_deg=144.0,
        closed=True,
        rotate_start=10,
        drop_last=True,
        vertices=(
            (3965.3122, 1527.6724),
            (3976.1809, 1527.6724),
            (3976.1809, 1530.0694),
            (3973.1809, 1535.2655),
            (3973.1809, 1547.5019),
            (3975.3884, 1551.3255),
            (3971.1437, 1554.4094),
            (3964.6193, 1545.4293),
            (3970.2824, 1541.3148),
            (3964.6395, 1533.5480),
            (3963.9923, 1534.0182),
            (3969.1650, 1541.1379),
            (3963.5019, 1545.2524),
            (3970.9667, 1555.5269),
            (3976.4414, 1551.5493),
            (3973.9809, 1547.2876),
            (3973.9809, 1535.4799),
            (3976.9809, 1530.2837),
            (3976.9809, 1526.8724),
            (3964.9809, 1526.8724),
            (3955.2727, 1533.9258),
            (3958.7994, 1538.7799),
            (3964.3007, 1534.7830),
            (3963.8305, 1534.1358),
            (3958.9764, 1537.6625),
            (3956.3901, 1534.1028),
            (3965.1830, 1527.7144),
        ),
    ),
)

PROFILE_SAMPLES: tuple[Sample, ...] = (
    Sample(
        angle_deg=91.0,
        closed=True,
        vertices=(
            (4069.2655, 1422.6954),
            (4078.0645, 1422.8489),
            (4077.9423, 1429.8479),
            (4090.6404, 1430.0695),
            (4090.7337, 1424.7232),
            (4087.6793, 1419.4328),
            (4087.6793, 1407.6251),
            (4090.6793, 1402.4289),
            (4090.6793, 1399.0176),
            (4064.4780, 1399.0176),
            (4063.9441, 1429.6035),
            (4069.9432, 1429.7083),
            (4070.0619, 1422.9093),
        ),
    ),
    Sample(
        angle_deg=105.0,
        closed=True,
        vertices=(
            (3906.8157, 1617.3219),
            (3915.3162, 1619.5996),
            (3913.5045, 1626.3610),
            (3925.7717, 1629.6480),
            (3927.1557, 1624.4831),
            (3924.1013, 1619.1927),
            (3924.1013, 1607.3850),
            (3927.1013, 1602.1888),
            (3927.1013, 1598.7775),
            (3906.4012, 1598.7775),
            (3899.9812, 1622.7375),
            (3905.7767, 1624.2904),
            (3907.5367, 1617.7221),
        ),
    ),
    Sample(
        angle_deg=116.0,
        closed=True,
        vertices=(
            (4111.3565, 1607.5617),
            (4119.2662, 1611.4195),
            (4116.1976, 1617.7111),
            (4127.6123, 1623.2784),
            (4129.9563, 1618.4724),
            (4126.9019, 1613.1820),
            (4126.9019, 1601.3743),
            (4129.9019, 1596.1782),
            (4129.9019, 1592.7668),
            (4112.7869, 1592.7668),
            (4103.6141, 1611.5738),
            (4109.0069, 1614.2040),
            (4111.9878, 1608.0922),
        ),
    ),
    Sample(
        angle_deg=135.0,
        closed=True,
        vertices=(
            (3814.2583, 1608.5251),
            (3813.6926, 1609.0908),
            (3819.9154, 1615.3136),
            (3814.9656, 1620.2634),
            (3823.9459, 1629.2436),
            (3827.7269, 1625.4626),
            (3824.6725, 1620.1722),
            (3824.6725, 1608.3645),
            (3827.6725, 1603.1684),
            (3827.6725, 1599.7570),
            (3815.6725, 1599.7570),
            (3805.0659, 1610.3636),
            (3809.3085, 1614.6063),
        ),
    ),
    Sample(
        angle_deg=144.0,
        closed=True,
        vertices=(
            (3963.9923, 1534.0182),
            (3969.1650, 1541.1379),
            (3963.5019, 1545.2524),
            (3970.9667, 1555.5269),
            (3976.4414, 1551.5493),
            (3973.9809, 1547.2876),
            (3973.9809, 1535.4799),
            (3976.9809, 1530.2837),
            (3976.9809, 1526.8724),
            (3964.9809, 1526.8724),
            (3955.2727, 1533.9258),
            (3958.7994, 1538.7799),
            (3964.3007, 1534.7830),
        ),
    ),
    Sample(
        angle_deg=165.0,
        closed=True,
        vertices=(
            (3853.7705, 1607.0900),
            (3856.0482, 1615.5905),
            (3849.2867, 1617.4023),
            (3852.5737, 1629.6695),
            (3868.6621, 1625.3587),
            (3866.2543, 1621.1883),
            (3866.2543, 1609.3806),
            (3869.2543, 1604.1845),
            (3869.2543, 1600.7731),
            (3857.2543, 1600.7731),
            (3845.6632, 1603.8790),
            (3847.2161, 1609.6745),
            (3853.7844, 1607.9146),
        ),
    ),
    Sample(
        angle_deg=179.0,
        closed=True,
        vertices=(
            (4312.7332, 1507.6278),
            (4312.8868, 1516.4268),
            (4305.8879, 1516.5489),
            (4306.1095, 1529.2470),
            (4330.1468, 1528.8274),
            (4326.6417, 1522.7565),
            (4326.6417, 1510.9488),
            (4329.6417, 1505.7526),
            (4329.6417, 1502.3413),
            (4317.6417, 1502.3413),
            (4305.6435, 1502.5507),
            (4305.7482, 1508.5498),
            (4312.5472, 1508.4311),
        ),
    ),
)

MODELS: dict[str, ModelSpec] = {
    "constructive-profile": ModelSpec(
        key="constructive-profile",
        generator="constructive-profile-v2",
        samples=PROFILE_SAMPLES,
        validate_simple=True,
    ),
    "profile": ModelSpec(
        key="profile",
        generator="outer-profile-v2",
        samples=PROFILE_SAMPLES,
        validate_simple=False,
    ),
    "legacy-full": ModelSpec(
        key="legacy-full",
        generator="legacy-full-v1",
        samples=LEGACY_FULL_SAMPLES,
        validate_simple=True,
    ),
}

RIGHT_PROFILE_OUTER_SAMPLES: tuple[Sample, ...] = (
    Sample(
        angle_deg=91.0,
        closed=False,
        vertices=(
            (3808.7572, 1737.9404),
            (3798.7587, 1738.1149),
            (3798.8809, 1745.1138),
            (3788.1825, 1745.3006),
            (3788.1046, 1740.8377),
            (3788.1046, 1734.3933),
            (3792.6046, 1734.3933),
            (3792.6046, 1724.7933),
            (3788.1046, 1724.7933),
            (3788.1046, 1715.8852),
            (3812.7729, 1715.8852),
            (3813.2787, 1744.8625),
            (3808.8794, 1744.9393),
            (3808.7782, 1739.1402),
        ),
    ),
    Sample(
        angle_deg=100.0,
        closed=False,
        vertices=(
            (3381.6648, 1706.1096),
            (3371.8168, 1707.8461),
            (3373.0323, 1714.7397),
            (3362.4949, 1716.5978),
            (3361.7307, 1712.2640),
            (3361.7307, 1705.8827),
            (3366.2307, 1705.8827),
            (3366.2307, 1696.2827),
            (3361.7307, 1696.2827),
            (3361.7307, 1687.3745),
            (3382.8292, 1687.3745),
            (3387.2135, 1712.2392),
            (3382.8804, 1713.0032),
            (3381.8732, 1707.2914),
        ),
    ),
    Sample(
        angle_deg=110.0,
        closed=False,
        vertices=(
            (3274.3584, 1694.7031),
            (3264.9615, 1698.1233),
            (3267.3556, 1704.7012),
            (3257.3009, 1708.3608),
            (3255.8201, 1704.2924),
            (3255.8201, 1697.9821),
            (3260.3201, 1697.9821),
            (3260.3201, 1688.3821),
            (3255.8201, 1688.3821),
            (3255.8201, 1679.4740),
            (3273.4978, 1679.4740),
            (3280.8872, 1699.7761),
            (3276.7525, 1701.2810),
            (3274.7688, 1695.8308),
        ),
    ),
    Sample(
        angle_deg=135.0,
        closed=False,
        vertices=(
            (4564.4805, 1548.7510),
            (4557.4094, 1555.8221),
            (4562.3592, 1560.7718),
            (4554.7931, 1568.3379),
            (4551.8663, 1565.4110),
            (4551.8663, 1559.2911),
            (4556.3663, 1559.2911),
            (4556.3663, 1549.6911),
            (4551.8663, 1549.6911),
            (4551.8663, 1540.7829),
            (4562.7349, 1540.7829),
            (4572.5415, 1550.5895),
            (4569.4302, 1553.7008),
            (4565.3290, 1549.5996),
        ),
    ),
    Sample(
        angle_deg=160.0,
        closed=False,
        vertices=(
            (3359.3781, 1685.5565),
            (3355.9579, 1694.9534),
            (3362.5358, 1697.3476),
            (3358.8762, 1707.4023),
            (3343.1909, 1701.6933),
            (3343.1909, 1697.5158),
            (3347.6909, 1697.5158),
            (3347.6909, 1687.9158),
            (3343.1909, 1687.9158),
            (3343.1909, 1679.0076),
            (3354.2498, 1679.0076),
            (3367.4609, 1683.8160),
            (3365.9560, 1687.9507),
            (3360.5058, 1685.9669),
        ),
    ),
    Sample(
        angle_deg=170.0,
        closed=False,
        vertices=(
            (3343.2206, 1692.9071),
            (3341.4841, 1702.7552),
            (3348.3778, 1703.9708),
            (3346.5197, 1714.5082),
            (3328.7874, 1711.3815),
            (3328.7874, 1706.3650),
            (3333.2874, 1706.3650),
            (3333.2874, 1696.7650),
            (3328.7874, 1696.7650),
            (3328.7874, 1687.8568),
            (3339.9174, 1687.8568),
            (3350.8783, 1689.7895),
            (3350.1142, 1694.1227),
            (3344.4024, 1693.1155),
        ),
    ),
    Sample(
        angle_deg=179.0,
        closed=False,
        vertices=(
            (3853.2439, 1721.1142),
            (3853.0693, 1731.1127),
            (3860.0683, 1731.2349),
            (3859.8815, 1741.9333),
            (3837.9353, 1741.5502),
            (3837.9353, 1735.1499),
            (3842.4353, 1735.1499),
            (3842.4353, 1725.5499),
            (3837.9353, 1725.5499),
            (3837.9353, 1716.6417),
            (3849.1283, 1716.6417),
            (3860.3196, 1716.8371),
            (3860.2428, 1721.2364),
            (3854.4437, 1721.1352),
        ),
    ),
)


def to_local_uv(vertices: tuple[Point, ...], angle_deg: float) -> list[Point]:
    angle = math.radians(angle_deg)
    u = (math.cos(angle), math.sin(angle))
    v = (math.cos(angle - math.pi / 2.0), math.sin(angle - math.pi / 2.0))
    ox, oy = vertices[0]

    result: list[Point] = []
    for x, y in vertices:
        dx = x - ox
        dy = y - oy
        uu = dx * u[0] + dy * u[1]
        vv = dx * v[0] + dy * v[1]
        result.append((uu, vv))
    return result


def _world_point(x: float, y: float) -> Point:
    return (x, y)


def _point_add(point: Point, vector: Point) -> Point:
    return (point[0] + vector[0], point[1] + vector[1])


def _point_sub(a: Point, b: Point) -> Point:
    return (a[0] - b[0], a[1] - b[1])


def _point_scale(vector: Point, scale: float) -> Point:
    return (vector[0] * scale, vector[1] * scale)


def _length(vector: Point) -> float:
    return math.hypot(vector[0], vector[1])


def _unit_world(angle_deg: float) -> Point:
    angle = math.radians(angle_deg)
    return (math.cos(angle), math.sin(angle))


def _ray_intersection(point_a: Point, angle_a_deg: float, point_b: Point, angle_b_deg: float) -> Point:
    dir_a = _unit_world(angle_a_deg)
    dir_b = _unit_world(angle_b_deg)
    determinant = dir_a[0] * dir_b[1] - dir_a[1] * dir_b[0]
    if abs(determinant) < 1e-9:
        raise ValueError("Constructive rays became parallel. Angle is outside the stable range.")

    delta = _point_sub(point_b, point_a)
    scale_a = (delta[0] * dir_b[1] - delta[1] * dir_b[0]) / determinant
    return _point_add(point_a, _point_scale(dir_a, scale_a))


def _constructive_target_top(angle_deg: float) -> float:
    if angle_deg <= 135.0:
        return 5.3471
    if angle_deg <= 144.0:
        return 5.3471 + (6.7670 - 5.3471) * (angle_deg - 135.0) / 9.0
    if angle_deg <= 165.0:
        return 6.7670 + (16.6560 - 6.7670) * (angle_deg - 144.0) / 21.0
    return 16.6560 + (24.0410 - 16.6560) * (angle_deg - 165.0) / 14.0


def _constructive_target_l15(angle_deg: float) -> float:
    if angle_deg <= 135.0:
        return (
            -0.0000240535429 * angle_deg * angle_deg * angle_deg
            + 0.00992590021 * angle_deg * angle_deg
            - 1.66452428 * angle_deg
            + 117.991982
        )
    if angle_deg <= 144.0:
        return 15.0 - 3.0 * (angle_deg - 135.0) / 9.0
    return 12.0


def _constructive_l5_hint(angle_deg: float) -> float:
    if angle_deg <= 135.0:
        return 6.1088
    if angle_deg <= 144.0:
        return 6.1088 + (4.9206 - 6.1088) * (angle_deg - 135.0) / 9.0
    if angle_deg <= 165.0:
        return 4.9206 + (4.8160 - 4.9206) * (angle_deg - 144.0) / 21.0
    return 4.8160 + (7.0101 - 4.8160) * (angle_deg - 165.0) / 14.0


def _constructive_world_vertices(angle_deg: float, l5_length: float, top_length: float) -> list[Point]:
    u = _unit_world(angle_deg)
    v = _unit_world(angle_deg - 90.0)

    p0 = _world_point(0.0, 0.0)
    p1 = _point_add(p0, _point_scale(v, 8.8))
    p2 = _point_add(p1, _point_scale(u, 7.0))
    p3 = _point_add(p2, _point_scale(v, 12.7))
    p4 = _point_add(p3, _point_scale(u, -top_length))
    p5 = _point_add(p4, _point_scale(_unit_world(240.0), l5_length))
    p6 = _point_add(p5, _point_scale(_unit_world(270.0), 11.8077))
    p7 = _point_add(p6, _point_scale(_unit_world(300.0), 6.0))
    p8 = _point_add(p7, _point_scale(_unit_world(270.0), 3.4114))

    seam = _point_add(_point_scale(u, 0.2), _point_scale(v, 0.8))
    p12 = _point_add(p0, seam)
    p11 = _point_add(p12, _point_scale(u, 6.8))
    p10 = _point_add(p11, _point_scale(v, -6.0))
    p9 = _ray_intersection(p8, 180.0, p10, angle_deg + 180.0)

    return [p0, p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12]


def _constructive_l15_for_l5(angle_deg: float, l5_length: float, top_length: float) -> float:
    vertices = _constructive_world_vertices(angle_deg, l5_length, top_length)
    p9 = vertices[9]
    p10 = vertices[10]
    return _length(_point_sub(p10, p9))


def _solve_constructive_l5(angle_deg: float, top_length: float, target_l15: float) -> float:
    hint = _constructive_l5_hint(angle_deg)
    scan_values = [4.0 + index * 0.025 for index in range(int((8.0 - 4.0) / 0.025) + 1)]
    scan_errors = [
        _constructive_l15_for_l5(angle_deg, value, top_length) - target_l15
        for value in scan_values
    ]

    candidate_roots: list[float] = []
    for index in range(len(scan_values) - 1):
        left_value = scan_values[index]
        right_value = scan_values[index + 1]
        left_error = scan_errors[index]
        right_error = scan_errors[index + 1]

        if abs(left_error) < 1e-6:
            candidate_roots.append(left_value)
            continue
        if left_error * right_error > 0:
            continue

        low = left_value
        high = right_value
        low_error = left_error
        for _ in range(60):
            mid = (low + high) / 2.0
            mid_error = _constructive_l15_for_l5(angle_deg, mid, top_length) - target_l15
            if abs(mid_error) < 1e-9:
                low = high = mid
                break
            if low_error * mid_error <= 0:
                high = mid
            else:
                low = mid
                low_error = mid_error
        candidate_roots.append((low + high) / 2.0)

    if candidate_roots:
        return min(candidate_roots, key=lambda value: abs(value - hint))

    return min(
        scan_values,
        key=lambda value: abs(_constructive_l15_for_l5(angle_deg, value, top_length) - target_l15),
    )


def _offset_open_polyline(points: list[Point], offset: float) -> list[Point]:
    if len(points) < 2:
        raise ValueError("Need at least two points to offset a polyline.")

    normals: list[Point] = []
    for start, end in zip(points, points[1:]):
        direction = _point_sub(end, start)
        length = _length(direction)
        if length < 1e-9:
            raise ValueError("Degenerate edge encountered during offset construction.")
        unit = (direction[0] / length, direction[1] / length)
        # The constructive outer chain runs clockwise. Right offset goes inward.
        normals.append((unit[1], -unit[0]))

    result: list[Point] = []
    result.append(_point_add(points[0], _point_scale(normals[0], offset)))
    for index in range(1, len(points) - 1):
        previous_normal = normals[index - 1]
        next_normal = normals[index]
        previous_start = _point_add(points[index - 1], _point_scale(previous_normal, offset))
        previous_end = _point_add(points[index], _point_scale(previous_normal, offset))
        next_start = _point_add(points[index], _point_scale(next_normal, offset))
        next_end = _point_add(points[index + 1], _point_scale(next_normal, offset))
        result.append(_ray_intersection(previous_start, math.degrees(math.atan2(previous_end[1] - previous_start[1], previous_end[0] - previous_start[0])), next_start, math.degrees(math.atan2(next_end[1] - next_start[1], next_end[0] - next_start[0]))))

    result.append(_point_add(points[-1], _point_scale(normals[-1], offset)))
    return result


def build_constructive_outer_vertices_uv(angle_deg: float) -> list[Point]:
    model_name = "constructive-profile"
    min_angle, max_angle = get_model_range(model_name)
    if angle_deg < min_angle or angle_deg > max_angle:
        raise ValueError(
            f"Model '{model_name}' currently supports {min_angle:.0f} to {max_angle:.0f} degrees."
        )

    top_length = _constructive_target_top(angle_deg)
    target_l15 = _constructive_target_l15(angle_deg)
    l5_length = _solve_constructive_l5(angle_deg, top_length, target_l15)
    world_vertices = _constructive_world_vertices(angle_deg, l5_length, top_length)
    return to_local_uv(tuple(world_vertices), angle_deg)


def build_constructive_vertices_uv(angle_deg: float) -> list[Point]:
    outer_uv = build_constructive_outer_vertices_uv(angle_deg)
    outer_world = from_local_uv(outer_uv, angle_deg, 0.0, 0.0)
    inner_world = list(reversed(_offset_open_polyline(outer_world, 0.8)))
    full_world = outer_world + inner_world
    full_uv = to_local_uv(tuple(full_world), angle_deg)
    validate_closed_polyline(from_local_uv(full_uv, angle_deg, 0.0, 0.0))
    return full_uv


def from_local_uv(vertices_uv: list[Point], angle_deg: float, base_x: float, base_y: float) -> list[Point]:
    angle = math.radians(angle_deg)
    u = (math.cos(angle), math.sin(angle))
    v = (math.cos(angle - math.pi / 2.0), math.sin(angle - math.pi / 2.0))

    result: list[Point] = []
    for uu, vv in vertices_uv:
        x = base_x + uu * u[0] + vv * v[0]
        y = base_y + uu * u[1] + vv * v[1]
        result.append((x, y))
    return result


def build_anchor_map(samples: tuple[Sample, ...]) -> dict[float, list[Point]]:
    return {
        sample.angle_deg: to_local_uv(sample.normalized_vertices(), sample.angle_deg)
        for sample in samples
    }


MODEL_ANCHOR_MAPS: dict[str, dict[float, list[Point]]] = {
    key: build_anchor_map(spec.samples) for key, spec in MODELS.items()
}
MODEL_ANCHOR_ANGLES: dict[str, list[float]] = {
    key: sorted(anchor_map.keys()) for key, anchor_map in MODEL_ANCHOR_MAPS.items()
}
RIGHT_PROFILE_ANCHOR_MAP = build_anchor_map(RIGHT_PROFILE_OUTER_SAMPLES)
RIGHT_PROFILE_ANGLES = sorted(RIGHT_PROFILE_ANCHOR_MAP.keys())
RIGHT_PROFILE_BRANCH_PIVOT = 135.0
RIGHT_PROFILE_FIXED_MID_SPAN = 18.5082
RIGHT_PROFILE_MIN_TOP_SPAN = 5.3
RIGHT_PROFILE_TARGET_RIGHT_LOWER_SPAN = 12.0
RIGHT_PROFILE_TOP_SPAN_SEARCH_MARGIN = 8.0
RIGHT_PROFILE_TARGET_TOP_DISPLAY_SPAN = 7.95
RIGHT_PROFILE_TARGET_BOTTOM_DISPLAY_SPAN = 12.0
RIGHT_PROFILE_TARGET_RIGHT_DISPLAY_SPAN = 12.0


@dataclass(frozen=True)
class RightProfileControls:
    top_span: float
    side_span: float


def _right_segment_length(vertices: list[Point], start_index: int, end_index: int) -> float:
    return _length(_point_sub(vertices[end_index], vertices[start_index]))


def _extract_right_profile_controls(vertices: list[Point]) -> RightProfileControls:
    return RightProfileControls(
        top_span=_right_segment_length(vertices, 3, 4),
        side_span=_right_segment_length(vertices, 4, 5),
    )


RIGHT_PROFILE_CONTROL_MAP = {
    angle: _extract_right_profile_controls(vertices)
    for angle, vertices in RIGHT_PROFILE_ANCHOR_MAP.items()
}
RIGHT_PROFILE_LOWER_ANGLES = [angle for angle in RIGHT_PROFILE_ANGLES if angle <= RIGHT_PROFILE_BRANCH_PIVOT]
RIGHT_PROFILE_UPPER_ANGLES = [angle for angle in RIGHT_PROFILE_ANGLES if angle >= RIGHT_PROFILE_BRANCH_PIVOT]


def _right_branch_angles(angle_deg: float) -> list[float]:
    return RIGHT_PROFILE_LOWER_ANGLES if angle_deg < RIGHT_PROFILE_BRANCH_PIVOT else RIGHT_PROFILE_UPPER_ANGLES


def get_model(model_name: str) -> ModelSpec:
    try:
        return MODELS[model_name]
    except KeyError as exc:
        raise ValueError(
            f"Unknown model '{model_name}'. Available models: {', '.join(sorted(MODELS))}."
        ) from exc


def get_model_range(model_name: str) -> tuple[float, float]:
    angles = MODEL_ANCHOR_ANGLES[model_name]
    return angles[0], angles[-1]


def _ccw(a: Point, b: Point, c: Point) -> bool:
    return (c[1] - a[1]) * (b[0] - a[0]) > (b[1] - a[1]) * (c[0] - a[0])


def _segments_intersect(a: Point, b: Point, c: Point, d: Point) -> bool:
    return _ccw(a, c, d) != _ccw(b, c, d) and _ccw(a, b, c) != _ccw(a, b, d)


def validate_closed_polyline(vertices: list[Point]) -> None:
    count = len(vertices)
    for i in range(count):
        a = vertices[i]
        b = vertices[(i + 1) % count]
        for j in range(i + 1, count):
            if abs(i - j) <= 1 or (i == 0 and j == count - 1):
                continue
            c = vertices[j]
            d = vertices[(j + 1) % count]
            if _segments_intersect(a, b, c, d):
                raise ValueError(
                    f"Generated polyline self-intersects between edges {i + 1} and {j + 1}."
                )


def _lagrange_interpolate(angle_deg: float, branch_angles: list[float], values: list[float]) -> float:
    total = 0.0
    for index, anchor_angle in enumerate(branch_angles):
        term = values[index]
        for other_index, other_angle in enumerate(branch_angles):
            if index == other_index:
                continue
            term *= (angle_deg - other_angle) / (anchor_angle - other_angle)
        total += term
    return total


def _interpolate_right_controls(angle_deg: float) -> RightProfileControls:
    if angle_deg < RIGHT_PROFILE_ANGLES[0] or angle_deg > RIGHT_PROFILE_ANGLES[-1]:
        raise ValueError(
            f"Right profile currently supports {RIGHT_PROFILE_ANGLES[0]:.0f} to "
            f"{RIGHT_PROFILE_ANGLES[-1]:.0f} degrees."
        )

    if angle_deg in RIGHT_PROFILE_CONTROL_MAP and angle_deg <= RIGHT_PROFILE_BRANCH_PIVOT:
        return RIGHT_PROFILE_CONTROL_MAP[angle_deg]

    branch_angles = _right_branch_angles(angle_deg)
    top_values = [RIGHT_PROFILE_CONTROL_MAP[angle].top_span for angle in branch_angles]
    side_values = [RIGHT_PROFILE_CONTROL_MAP[angle].side_span for angle in branch_angles]
    controls = RightProfileControls(
        top_span=_lagrange_interpolate(angle_deg, branch_angles, top_values),
        side_span=_lagrange_interpolate(angle_deg, branch_angles, side_values),
    )
    return _constrain_right_controls(angle_deg, controls)


def _right_profile_points_for_controls(
    angle_deg: float,
    top_span: float,
    side_span: float,
) -> tuple[Point, Point, Point, Point, Point, Point, Point, Point, Point, Point, Point, Point, Point, Point]:
    dir_a = 2.0 * angle_deg - 270.0
    dir_b = 2.0 * angle_deg - 180.0
    dir_c = 2.0 * angle_deg - 360.0
    dir_d = angle_deg - 270.0
    dir_e = angle_deg
    dir_f = angle_deg + 90.0
    dir_g = angle_deg + 180.0

    p1 = (0.0, 0.0)
    p2 = _point_add(p1, _point_scale(_unit_world(dir_a), 10.0))
    p3 = _point_add(p2, _point_scale(_unit_world(dir_b), 7.0))
    p4 = _point_add(p3, _point_scale(_unit_world(dir_a), 10.7))
    p5 = _point_add(p4, _point_scale(_unit_world(dir_c), top_span))
    p6 = _point_add(p5, _point_scale(_unit_world(dir_d), side_span))
    p7 = _point_add(p6, _point_scale(_unit_world(dir_e), 4.5))
    p8 = _point_add(p7, _point_scale(_unit_world(dir_f), 9.6))
    p9 = _point_add(p8, _point_scale(_unit_world(dir_g), 4.5))
    p10 = _point_add(p9, _point_scale(_unit_world(dir_f), 8.9082))
    p14 = _point_add(p1, _point_scale(_unit_world(dir_b), 1.2))
    p13 = _point_add(p14, _point_scale(_unit_world(dir_b), 5.8))
    p12 = _point_add(p13, _point_scale(_unit_world(dir_a + 180.0), 4.4))
    p11 = _ray_intersection(p10, dir_e, p12, dir_b + 180.0)

    return (p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14)


def _right_lower_span_for_controls(angle_deg: float, top_span: float, side_span: float) -> float:
    points = _right_profile_points_for_controls(angle_deg, top_span, side_span)
    return _length(_point_sub(points[10], points[11]))


def _right_both_edges_for_controls(angle_deg: float, top_span: float, side_span: float) -> tuple[float, float]:
    """Returns (left_edge_length, right_edge_length) for given controls."""
    points = _right_profile_points_for_controls(angle_deg, top_span, side_span)
    p10, p11, p12 = points[9], points[10], points[11]
    left_edge = _length(_point_sub(p11, p10))
    right_edge = _length(_point_sub(p11, p12))
    return left_edge, right_edge


def _right_display_spans_for_controls(angle_deg: float, top_span: float, side_span: float) -> tuple[float, float, float]:
    """Returns displayed inner spans as (top, bottom, right) for the right profile.

    The displayed dimensions are measured on full 28-vertex output:
    - top: edge 24->25
    - bottom: edge 18->19
    - right: edge 17->18
    """
    outer_uv = list(_right_profile_points_for_controls(angle_deg, top_span, side_span))
    outer_world = from_local_uv(outer_uv, angle_deg, 0.0, 0.0)
    inner_world = list(reversed(_offset_open_polyline(outer_world, 0.8)))
    full_world = outer_world + inner_world
    validate_closed_polyline(full_world)
    full_uv = to_local_uv(tuple(full_world), angle_deg)

    def segment_length(start_index_1based: int, end_index_1based: int) -> float:
        a = full_uv[start_index_1based - 1]
        b = full_uv[end_index_1based - 1]
        return _length(_point_sub(b, a))

    top_span_display = segment_length(24, 25)
    bottom_span_display = segment_length(18, 19)
    right_span_display = segment_length(17, 18)
    return top_span_display, bottom_span_display, right_span_display


def _solve_right_display_targets(angle_deg: float, hint_top_span: float, hint_side_span: float) -> RightProfileControls:
    """2D grid search to match displayed right-profile dimensions.

    Targets:
    - top displayed span (24->25): ~7.95
    - bottom displayed span (18->19): ~12
    - right displayed span (17->18): ~12
    """
    target_top = RIGHT_PROFILE_TARGET_TOP_DISPLAY_SPAN
    target_bottom = RIGHT_PROFILE_TARGET_BOTTOM_DISPLAY_SPAN
    target_right = RIGHT_PROFILE_TARGET_RIGHT_DISPLAY_SPAN
    min_top = RIGHT_PROFILE_MIN_TOP_SPAN  # 5.3
    
    # Use a wide global search first because displayed targets can require
    # much smaller top_span than interpolation hints around 145°.
    top_min = min_top
    top_max = max(hint_top_span, min_top) + RIGHT_PROFILE_TOP_SPAN_SEARCH_MARGIN
    side_min = 1.0
    side_max = 10.0
    
    best_primary_error = float('inf')
    best_secondary_error = float('inf')
    best_solution = (hint_top_span, hint_side_span)
    
    def evaluate_grid(
        top_start: float,
        top_end: float,
        top_step: float,
        side_start: float,
        side_end: float,
        side_step: float,
    ) -> None:
        nonlocal best_primary_error, best_secondary_error, best_solution
        top = top_start
        while top <= top_end + 1e-9:
            side = side_start
            while side <= side_end + 1e-9:
                try:
                    display_top, display_bottom, display_right = _right_display_spans_for_controls(angle_deg, top, side)
                    primary_error = (
                        (display_bottom - target_bottom) ** 2
                        + (display_right - target_right) ** 2
                    )
                    secondary_error = (display_top - target_top) ** 2
                    if (
                        primary_error < best_primary_error - 1e-12
                        or (
                            abs(primary_error - best_primary_error) <= 1e-12
                            and secondary_error < best_secondary_error
                        )
                    ):
                        best_primary_error = primary_error
                        best_secondary_error = secondary_error
                        best_solution = (top, side)
                except Exception:
                    pass
                side += side_step
            top += top_step

    # Coarse global pass (finer step avoids missing narrow feasible regions).
    evaluate_grid(top_min, top_max, 0.1, side_min, side_max, 0.05)

    # Fine local pass around current best.
    best_top, best_side = best_solution
    fine_top_min = max(min_top, best_top - 0.4)
    fine_top_max = best_top + 0.4
    fine_side_min = max(1.0, best_side - 0.4)
    fine_side_max = min(10.0, best_side + 0.4)
    evaluate_grid(fine_top_min, fine_top_max, 0.02, fine_side_min, fine_side_max, 0.02)
    
    best_top, best_side = best_solution
    return RightProfileControls(
        top_span=max(min_top, best_top),
        side_span=best_side
    )


def _solve_right_outer_edge_targets(angle_deg: float, hint_top_span: float, hint_side_span: float) -> RightProfileControls:
    """2D grid search for outer-edge targets (p10->p11 and p12->p11) ~= 12mm."""
    target = RIGHT_PROFILE_TARGET_RIGHT_LOWER_SPAN
    min_top = RIGHT_PROFILE_MIN_TOP_SPAN

    top_min = min_top
    top_max = max(hint_top_span, min_top) + RIGHT_PROFILE_TOP_SPAN_SEARCH_MARGIN
    side_min = 1.0
    side_max = 10.0

    best_error = float("inf")
    best_solution = (hint_top_span, hint_side_span)

    def evaluate_grid(
        top_start: float,
        top_end: float,
        top_step: float,
        side_start: float,
        side_end: float,
        side_step: float,
    ) -> None:
        nonlocal best_error, best_solution
        top = top_start
        while top <= top_end + 1e-9:
            side = side_start
            while side <= side_end + 1e-9:
                try:
                    left_edge, right_edge = _right_both_edges_for_controls(angle_deg, top, side)
                    error = (left_edge - target) ** 2 + (right_edge - target) ** 2
                    if error < best_error:
                        best_error = error
                        best_solution = (top, side)
                except Exception:
                    pass
                side += side_step
            top += top_step

    evaluate_grid(top_min, top_max, 0.2, side_min, side_max, 0.1)

    best_top, best_side = best_solution
    fine_top_min = max(min_top, best_top - 0.4)
    fine_top_max = best_top + 0.4
    fine_side_min = max(1.0, best_side - 0.4)
    fine_side_max = min(10.0, best_side + 0.4)
    evaluate_grid(fine_top_min, fine_top_max, 0.02, fine_side_min, fine_side_max, 0.02)

    best_top, best_side = best_solution
    return RightProfileControls(
        top_span=max(min_top, best_top),
        side_span=best_side,
    )


def _constrain_right_controls(angle_deg: float, controls: RightProfileControls) -> RightProfileControls:
    if angle_deg <= RIGHT_PROFILE_BRANCH_PIVOT:
        return controls

    return _solve_right_display_targets(angle_deg, controls.top_span, controls.side_span)


def build_right_constructive_outer_vertices_uv(angle_deg: float) -> list[Point]:
    controls = _interpolate_right_controls(angle_deg)
    return list(_right_profile_points_for_controls(angle_deg, controls.top_span, controls.side_span))


def interpolate_right_outer_vertices_uv(angle_deg: float) -> list[Point]:
    if angle_deg < RIGHT_PROFILE_ANGLES[0] or angle_deg > RIGHT_PROFILE_ANGLES[-1]:
        raise ValueError(
            f"Right profile currently supports {RIGHT_PROFILE_ANGLES[0]:.0f} to "
            f"{RIGHT_PROFILE_ANGLES[-1]:.0f} degrees."
        )

    if angle_deg in RIGHT_PROFILE_ANCHOR_MAP:
        return list(RIGHT_PROFILE_ANCHOR_MAP[angle_deg])

    branch_angles = _right_branch_angles(angle_deg)
    right = bisect.bisect_right(branch_angles, angle_deg)
    left_angle = branch_angles[right - 1]
    right_angle = branch_angles[right]
    left_vertices = RIGHT_PROFILE_ANCHOR_MAP[left_angle]
    right_vertices = RIGHT_PROFILE_ANCHOR_MAP[right_angle]
    ratio = (angle_deg - left_angle) / (right_angle - left_angle)
    return [
        (
            left_point[0] + (right_point[0] - left_point[0]) * ratio,
            left_point[1] + (right_point[1] - left_point[1]) * ratio,
        )
        for left_point, right_point in zip(left_vertices, right_vertices)
    ]


def build_right_profile_vertices_uv(angle_deg: float) -> list[Point]:
    if angle_deg in RIGHT_PROFILE_ANCHOR_MAP and angle_deg <= RIGHT_PROFILE_BRANCH_PIVOT:
        outer_world = from_local_uv(RIGHT_PROFILE_ANCHOR_MAP[angle_deg], angle_deg, 0.0, 0.0)
        inner_world = list(reversed(_offset_open_polyline(outer_world, 0.8)))
        full_world = outer_world + inner_world
        validate_closed_polyline(full_world)
        return to_local_uv(tuple(full_world), angle_deg)

    last_error: Exception | None = None
    for outer_uv in (
        build_right_constructive_outer_vertices_uv(angle_deg),
        interpolate_right_outer_vertices_uv(angle_deg),
    ):
        try:
            outer_world = from_local_uv(outer_uv, angle_deg, 0.0, 0.0)
            inner_world = list(reversed(_offset_open_polyline(outer_world, 0.8)))
            full_world = outer_world + inner_world
            validate_closed_polyline(full_world)
            return to_local_uv(tuple(full_world), angle_deg)
        except Exception as exc:
            last_error = exc
    assert last_error is not None
    raise last_error


def interpolate_vertices(angle_deg: float, model_name: str = DEFAULT_MODEL_NAME) -> list[Point]:
    get_model(model_name)
    if model_name == "constructive-profile":
        return build_constructive_vertices_uv(angle_deg)

    anchor_map = MODEL_ANCHOR_MAPS[model_name]
    anchor_angles = MODEL_ANCHOR_ANGLES[model_name]

    if angle_deg < anchor_angles[0] or angle_deg > anchor_angles[-1]:
        raise ValueError(
            f"Model '{model_name}' currently supports "
            f"{anchor_angles[0]:.0f} to {anchor_angles[-1]:.0f} degrees."
        )

    if angle_deg in anchor_map:
        return list(anchor_map[angle_deg])

    right = bisect.bisect_right(anchor_angles, angle_deg)
    left_angle = anchor_angles[right - 1]
    right_angle = anchor_angles[right]

    left_vertices = anchor_map[left_angle]
    right_vertices = anchor_map[right_angle]
    if len(left_vertices) != len(right_vertices):
        raise ValueError("Anchor topology mismatch. This interval needs a dedicated model.")

    ratio = (angle_deg - left_angle) / (right_angle - left_angle)
    result: list[Point] = []
    for left_point, right_point in zip(left_vertices, right_vertices):
        uu = left_point[0] + (right_point[0] - left_point[0]) * ratio
        vv = left_point[1] + (right_point[1] - left_point[1]) * ratio
        result.append((uu, vv))
    return result


def _translate_vertices(vertices: list[Point], dx: float, dy: float) -> list[Point]:
    return [(x + dx, y + dy) for x, y in vertices]


def _bbox(vertices: list[Point]) -> tuple[float, float, float, float]:
    xs = [x for x, _ in vertices]
    ys = [y for _, y in vertices]
    return min(xs), min(ys), max(xs), max(ys)


def build_pair_jsqcad(
    left_angle_deg: float,
    right_angle_deg: float,
    gap_mm: float,
    base_x: float,
    base_y: float,
) -> dict[str, object]:
    left_uv = build_constructive_vertices_uv(left_angle_deg)
    left_vertices = from_local_uv(left_uv, left_angle_deg, 0.0, 0.0)
    right_uv = build_right_profile_vertices_uv(right_angle_deg)
    right_vertices = from_local_uv(right_uv, right_angle_deg, 0.0, 0.0)

    left_min_x, left_min_y, left_max_x, _ = _bbox(left_vertices)
    right_min_x, right_min_y, _, _ = _bbox(right_vertices)

    right_vertices = _translate_vertices(
        right_vertices,
        left_max_x + gap_mm - right_min_x,
        left_min_y - right_min_y,
    )

    left_min_x, left_min_y, _, _ = _bbox(left_vertices)
    dx = base_x - left_min_x
    dy = base_y - left_min_y
    left_vertices = _translate_vertices(left_vertices, dx, dy)
    right_vertices = _translate_vertices(right_vertices, dx, dy)

    return {
        "format": "JSQCAD/1.0",
        "units": "mm",
        "basePoint": [round(base_x, 4), round(base_y, 4)],
        "meta": {
            "generator": "8317-center-post-pair-v1",
            "series": "8317",
            "pivotCenterPostName": "转轴中柱",
            "pivotCenterPostModel": "constructive-profile-v2",
            "pivotCenterPostAngleDeg": round(left_angle_deg, 4),
            "pivotCenterPostAnchorsDeg": MODEL_ANCHOR_ANGLES["constructive-profile"],
            "waterStopCenterPostName": "挡水中柱",
            "waterStopCenterPostModel": "right-middle-profile-v4",
            "waterStopCenterPostAngleDeg": round(right_angle_deg, 4),
            "waterStopCenterPostAnchorsDeg": RIGHT_PROFILE_ANGLES,
            "gapMm": round(gap_mm, 4),
        },
        "entities": [
            {
                "type": "polyline",
                "name": "转轴中柱",
                "layer": "0",
                "closed": True,
                "vertices": [[round(x, 4), round(y, 4)] for x, y in left_vertices],
            },
            {
                "type": "polyline",
                "name": "挡水中柱",
                "layer": "0",
                "closed": True,
                "vertices": [[round(x, 4), round(y, 4)] for x, y in right_vertices],
            },
        ],
    }


def build_jsqcad(
    angle_deg: float,
    base_x: float,
    base_y: float,
    model_name: str = DEFAULT_MODEL_NAME,
) -> dict[str, object]:
    model = get_model(model_name)
    anchor_angles = MODEL_ANCHOR_ANGLES[model_name]
    vertices_uv = interpolate_vertices(angle_deg, model_name)
    vertices_xy = from_local_uv(vertices_uv, angle_deg, base_x, base_y)
    if model.validate_simple:
        validate_closed_polyline(vertices_xy)
    return {
        "format": "JSQCAD/1.0",
        "units": "mm",
        "basePoint": [round(base_x, 4), round(base_y, 4)],
        "meta": {
            "generator": model.generator,
            "model": model.key,
            "angleDeg": round(angle_deg, 4),
            "anchorsDeg": anchor_angles,
        },
        "entities": [
            {
                "type": "polyline",
                "layer": "0",
                "closed": True,
                "vertices": [[round(x, 4), round(y, 4)] for x, y in vertices_xy],
            }
        ],
    }


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate JSQCAD from angle.")
    parser.add_argument("angle", type=float, help="Target angle in degrees.")
    parser.add_argument("--base-x", type=float, default=0.0, help="Insertion base X.")
    parser.add_argument("--base-y", type=float, default=0.0, help="Insertion base Y.")
    parser.add_argument("--right-angle", type=float, default=None, help="Optional water-stop center-post angle.")
    parser.add_argument("--gap", type=float, default=50.0, help="Gap between left and right profiles in pair mode.")
    parser.add_argument(
        "--model",
        choices=sorted(MODELS),
        default=DEFAULT_MODEL_NAME,
        help="Model branch. Default: %(default)s.",
    )
    parser.add_argument(
        "--prefix",
        action="store_true",
        help="Print with JSQCAD: prefix for direct clipboard use.",
    )
    parser.add_argument(
        "--mirror",
        action="store_true",
        help="Mirror all vertices horizontally (for right-axis mode).",
    )
    parser.add_argument(
        "--output",
        type=str,
        default="",
        help="Optional output file path. Defaults to stdout.",
    )
    return parser.parse_args(argv)


def _mirror_payload(payload: dict) -> dict:
    """Mirror all entity vertices horizontally and swap entity order."""
    all_xs: list[float] = []
    for entity in payload.get("entities", []):
        for v in entity.get("vertices", []):
            all_xs.append(v[0])
        if "center" in entity:
            all_xs.append(entity["center"][0])
        if "start" in entity:
            all_xs.append(entity["start"][0])
        if "end" in entity:
            all_xs.append(entity["end"][0])
    if not all_xs:
        return payload
    cx = (min(all_xs) + max(all_xs)) / 2.0
    for entity in payload.get("entities", []):
        if "vertices" in entity:
            entity["vertices"] = [[round(2 * cx - v[0], 4), v[1]] for v in entity["vertices"]]
        if "center" in entity:
            entity["center"] = [round(2 * cx - entity["center"][0], 4), entity["center"][1]]
        if "start" in entity:
            entity["start"] = [round(2 * cx - entity["start"][0], 4), entity["start"][1]]
        if "end" in entity:
            entity["end"] = [round(2 * cx - entity["end"][0], 4), entity["end"][1]]
    entities = payload.get("entities", [])
    if len(entities) == 2:
        entities[0], entities[1] = entities[1], entities[0]
    if "meta" in payload:
        payload["meta"]["mirrored"] = True
    return payload


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    if args.right_angle is None:
        payload = build_jsqcad(args.angle, args.base_x, args.base_y, args.model)
    else:
        payload = build_pair_jsqcad(args.angle, args.right_angle, args.gap, args.base_x, args.base_y)
    if args.mirror:
        payload = _mirror_payload(payload)
    text = json.dumps(payload, ensure_ascii=False, indent=2)
    if args.prefix:
        text = "JSQCAD:" + text

    if args.output:
        with open(args.output, "w", encoding="utf-8") as handle:
            handle.write(text)
            handle.write("\n")
    else:
        sys.stdout.write(text)
        sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
