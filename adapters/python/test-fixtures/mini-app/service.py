from domain import IRepository, Entity


class UserService:
    repo: IRepository

    def __init__(self, repo: IRepository) -> None:
        self.repo = repo

    def get_user(self, id: str) -> Entity:
        return self.repo.find_by_id(id)
