from abc import ABC, abstractmethod


class Entity:
    def __init__(self, id: str, name: str) -> None:
        self.id = id
        self.name = name


class IRepository(ABC):
    @abstractmethod
    def find_by_id(self, id: str) -> Entity:
        ...
